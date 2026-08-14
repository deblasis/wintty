using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ghostty.Core.Shell;

namespace Ghostty.Shell;

/// <summary>
/// Pre-XAML launch splash: a layered Win32 window showing the app icon,
/// created before <c>Application.Start</c> and torn down once the main
/// window has real content on screen.
///
/// <para>Why this is not XAML. WinUI 3 shows the main window's HWND
/// well before it composes a first XAML frame -- around two seconds on
/// a cold start -- and paints black for that whole gap. Nothing
/// declared in XAML can cover it, because XAML is precisely what is not
/// running yet. So the splash is a plain Win32 window on its own thread
/// with its own message pump, up within a few hundred milliseconds of
/// process start and owing nothing to the WinUI stack.</para>
///
/// <para>It covers the rect the main window is about to restore to --
/// resolved from the saved session or, failing that, the saved window
/// placement -- and fills it with the terminal background colour, so the
/// black gap is never visible. When the main window reports content, the
/// splash fades and closes, revealing the real window underneath.</para>
///
/// <para>Interop here is hand-written rather than CsWin32-generated for
/// the same reason <c>NativeMethods.txt</c> already documents for
/// CreateSolidBrush and SetClassLongPtr: several of these entry points
/// are absent from the CsWin32 metadata for this target. It follows the
/// project's <c>DisableRuntimeMarshalling</c> conventions -- Win32 BOOL
/// as <see cref="int"/>, strings as <see cref="char"/> pointers, every
/// struct blittable. The window procedure is an
/// <see cref="UnmanagedCallersOnlyAttribute"/> function pointer rather
/// than a delegate so there is no managed callback to keep alive; a
/// collected WNDPROC delegate is the classic way this kind of window
/// crashes.</para>
/// </summary>
internal static unsafe partial class SplashWindow
{
    // Fallback background when no colour has been persisted yet (first
    // ever launch). Matches ConfigService's default background so the
    // handoff to the real window is not a visible colour jump.
    private const uint DefaultBackgroundRgb = 0x1E1E2E;

    // Fallback rect when there is no saved window state, in physical
    // pixels. Close enough to the app's default window that the splash
    // lands where the window will.
    private const int FallbackWidth = 1200;
    private const int FallbackHeight = 800;

    private const int FadeDurationMs = 220;
    private const int FadeStepMs = 16;

    // How often to re-assert topmost while waiting. Frequent enough that a
    // main window appearing underneath is covered again within a frame or
    // two, rare enough not to load the window manager during startup.
    private const int TopmostNudgeIntervalMs = 250;

    // Hard stop. If the main window never reports content the splash must
    // still go away rather than sit on top of the user's desktop forever.
    private const int WatchdogMs = 10_000;

    // How long HideNow waits for the splash thread to unwind. Long enough
    // to cover a pump tick and the teardown behind it, short enough that a
    // wedged splash thread cannot noticeably delay an exit.
    private const int HideJoinMs = 250;

    private const string ClassName = "WinttySplash";

    private static nint _hwnd;
    private static readonly ManualResetEventSlim _dismissed = new(false);
    private static int _started;
    private static long _shownAtTicks;
    private static nint _trackedHwnd;

    // The splash thread, so HideNow can wait for it rather than leaving it to
    // be cut down wherever it happens to be. Volatile like the other fields
    // HideNow touches: it now runs from the unhandled-exception handlers,
    // which fire on whatever thread threw. Assigned before Start, so a HideNow
    // early enough to read null is also early enough that there is nothing yet
    // to wait for.
    private static Thread? _thread;

    // Set by HideNow to skip the fade. A fade is a courtesy on the normal
    // reveal; on the paths HideNow serves the process is about to end, and
    // 220ms of animation is 220ms of holding a window over the desktop.
    private static bool _skipFade;

    // Current splash geometry, owned by the splash thread. Mutable
    // because the splash follows the main window, which the user can move
    // or resize while it is still up.
    private static int _width;
    private static int _height;
    private static uint _background;

    // DPI of the monitor the splash is currently on. Mutable for the same
    // reason as the geometry above: the splash follows the main window, and
    // a window dragged onto a monitor at a different scale changes what a
    // pixel means without necessarily changing the rect.
    private static uint _dpi = 96;

    // Alpha of the last blend, so a repaint forced by something other than
    // the fade -- a resize, a scale change -- redraws at the opacity the
    // fade has reached rather than snapping back to fully opaque.
    private static byte _alpha = 255;

    // The composed splash bitmap, kept alive between blends. Building it
    // costs a full-window DIB, a per-pixel fill and a PNG decode, none of
    // which change while only the alpha does, so the fade re-blends this
    // instead of rebuilding it every frame. Owned by the splash thread and
    // released in RunSplash's finally.
    private static nint _surfaceScreenDc;
    private static nint _surfaceMemDc;
    private static nint _surfaceDib;
    private static nint _surfaceOldBitmap;
    private static int _surfaceWidth;
    private static int _surfaceHeight;
    private static uint _surfaceDpi;

    /// <summary>
    /// Follow <paramref name="hwnd"/> from now on. Called once the main
    /// window exists, so that moving or resizing it while the splash is
    /// still up keeps the two together instead of leaving the icon
    /// stranded where the window used to be. The alternative -- pinning
    /// the window in place until the splash goes -- would take a
    /// legitimate action away from the user to keep a decoration tidy.
    /// </summary>
    public static void Track(nint hwnd) => Volatile.Write(ref _trackedHwnd, hwnd);

    /// <summary>
    /// Milliseconds the splash has been on screen, or 0 if it was never
    /// shown. The dwell clause in <see cref="LaunchIconPolicy"/> is
    /// measured against this rather than against main-window
    /// construction, which happens seconds later.
    /// </summary>
    public static int VisibleForMs
    {
        get
        {
            var shown = Volatile.Read(ref _shownAtTicks);
            if (shown == 0) return 0;
            var elapsed = Environment.TickCount64 - shown;
            if (elapsed <= 0) return 0;
            return elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
        }
    }

    /// <summary>
    /// Put the splash on screen. Returns immediately; the window lives on
    /// its own background thread. Only the first call per process does
    /// anything.
    /// </summary>
    public static void Show()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        var thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "wintty-splash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        Volatile.Write(ref _thread, thread);
        thread.Start();
    }

    /// <summary>
    /// The main window has content. Fade the splash out and close it.
    /// Safe when no splash was ever shown, and safe to call repeatedly.
    /// </summary>
    public static void Dismiss()
    {
        // Set unconditionally rather than bailing when nothing has started
        // yet. RunSplash handles an already-signalled event correctly, so
        // latching here means a dismissal that races Show can never be
        // dropped and leave the splash up until the watchdog.
        _dismissed.Set();
    }

    /// <summary>
    /// Take the splash off the screen before returning, for callers whose
    /// next move is to end the process.
    /// </summary>
    /// <remarks>
    /// <see cref="Dismiss"/> is not enough there. It signals an event and the
    /// splash thread then fades over 220ms, so a caller that dismisses and
    /// exits is racing its own teardown: the window is still up when the
    /// process dies, and the splash thread is cut down wherever it happens to
    /// be, which can be inside GDI+.
    ///
    /// So this waits, and the wait is the whole mechanism. The window is taken
    /// down by the splash thread's own <c>DestroyWindow</c>, not from here:
    /// hiding another thread's window is a blocking inter-thread send, and
    /// posting it asynchronously instead only defers to a thread that has by
    /// then stopped pumping. Either way it is the splash thread that has to
    /// act, so the honest thing is to ask it to stop and wait a bounded time
    /// for it to finish. A wedged splash thread costs the deadline, never the
    /// caller's exit.
    ///
    /// Safe when no splash was shown and safe to call repeatedly.
    /// </remarks>
    public static void HideNow()
    {
        // Before the signal: a thread waking on the event must see the flag,
        // or it fades for 220ms while the caller waits on the join.
        Volatile.Write(ref _skipFade, true);
        _dismissed.Set();

        try
        {
            // Never join ourselves. Diag runs on the splash thread and could in
            // principle route back here; a self-join would hang the very exit
            // this method exists to make prompt.
            if (Volatile.Read(ref _thread) is { } thread && thread != Thread.CurrentThread)
                thread.Join(HideJoinMs);
        }
        catch
        {
            // A join that cannot be waited on is not a reason to fail an exit
            // path.
        }
    }

    /// <summary>
    /// Report a splash failure on the only channel that exists this early.
    /// Nothing here is worth failing the launch over, but a splash that
    /// silently does nothing -- or paints a bare rectangle because its
    /// icon is missing -- is otherwise invisible in the field.
    /// </summary>
    private static void Diag(string message)
    {
        try
        {
            Program.WriteStartupDiagnostic($"splash: {message}");
        }
        catch
        {
            // A last-chance reporter that can throw is worse than one that
            // says nothing: this runs on a background thread, where an
            // escape would take the process down over a decoration.
        }
    }

    private static void ThreadMain()
    {
        try
        {
            RunSplash();
        }
        catch (Exception ex)
        {
            // A splash is decoration. Nothing it can do is worth taking the
            // process down before the real window exists.
            Diag($"aborted: {ex}");
        }
    }

    private static void RunSplash()
    {
        var state = LoadState();
        var (x, y, width, height) = ResolveRect(state);
        _width = width;
        _height = height;
        _background = ResolveBackgroundRgb(state);

        if (!RegisterWindowClass())
        {
            Diag("could not register the window class");
            return;
        }

        // Do not build a window for a splash that has already been called
        // off. Show and HideNow can race on a startup that fails early, and
        // creating one here would put a window on screen after the caller
        // was told it was gone.
        if (_dismissed.IsSet) return;

        fixed (char* className = ClassName)
        {
            // WS_EX_TRANSPARENT is what keeps the app usable underneath.
            // Without it this window is a solid input target sitting over
            // the whole main window, so every click during the splash --
            // including any spent waiting for a surface that is already
            // ready -- lands here and does nothing, which reads as the app
            // being frozen. With it, hit-testing skips this window
            // entirely and input goes straight to the real one.
            _hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                    | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                className, null, WS_POPUP,
                x, y, width, height,
                0, 0, GetModuleHandleW(0), 0);
        }
        if (_hwnd == 0)
        {
            Diag($"CreateWindowExW failed for {width}x{height} at ({x},{y})");
            return;
        }

        try
        {
            // DPI is only knowable once the window exists and the OS has
            // assigned it to a monitor.
            AdoptDpi(GetDpiForWindow(_hwnd));

            if (!Paint(255))
            {
                Diag("first paint failed; no splash this launch");
                return;
            }
            // A dismissal may have landed since the check above. Narrows the
            // flash; HideNow's join is what actually closes it, by holding the
            // caller until the finally below destroys the window.
            if (_dismissed.IsSet) return;

            ShowWindow(_hwnd, SW_SHOWNA);
            Volatile.Write(ref _shownAtTicks, Environment.TickCount64);

            PumpUntilDismissed();
            FadeOut();
        }
        finally
        {
            // Clear the handle first. It is what WndProc matches on, and
            // DestroyWindow dispatches the messages the system has sent this
            // window -- so a WM_DPICHANGED arriving inside that call would
            // otherwise pass the guard and rebuild a full-window surface that
            // nothing is left to free. Zeroing first turns that into a no-op.
            var hwnd = _hwnd;
            _hwnd = 0;
            ReleaseSurface();
            DestroyWindow(hwnd);
        }
    }

    /// <summary>
    /// Service the message queue until dismissal or the watchdog. A
    /// window with no pump is marked unresponsive by the OS and painted
    /// as a ghost.
    /// </summary>
    private static void PumpUntilDismissed()
    {
        var deadline = Environment.TickCount64 + WatchdogMs;
        var nextTopmostNudge = 0L;

        while (!_dismissed.IsSet && Environment.TickCount64 < deadline)
        {
            // Re-assert topmost. Creating the window with WS_EX_TOPMOST is
            // not enough: when WinUI shows and activates the main window,
            // the splash ends up behind it and the black gap it is meant
            // to be covering shows through. SWP_NOACTIVATE so this never
            // steals focus from the window coming up behind it.
            //
            // Throttled rather than done every pump tick. At tick rate this
            // reorders the z-order sixty times a second while the main
            // thread is still initializing, and the resulting window-manager
            // and DWM work slows down the very startup we are waiting on.
            //
            // Skipped once the user has switched to another app. The splash
            // has no taskbar button, no Alt-Tab entry and no close affordance,
            // so re-asserting topmost over whatever they switched to would
            // leave them looking at something they cannot dismiss.
            var now = Environment.TickCount64;
            if (now >= nextTopmostNudge)
            {
                if (CoversForegroundWindow())
                {
                    SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                nextTopmostNudge = now + TopmostNudgeIntervalMs;
            }

            // Checked every tick, not throttled: this one tracks a drag,
            // so anything slower shows the splash lagging behind the
            // window. It is a GetWindowRect and, only on an actual
            // change, a SetWindowPos.
            FollowTrackedWindow();

            PumpMessages();
            // Waits on the dismiss signal rather than sleeping blindly, so
            // the fade starts the moment the window reports content.
            _dismissed.Wait(FadeStepMs);
        }
    }

    /// <summary>
    /// Drain the message queue. A window whose thread stops pumping is
    /// marked unresponsive by the OS and painted as a ghost.
    /// </summary>
    private static void PumpMessages()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>
    /// True while the splash is still covering for this app rather than
    /// sitting over something the user deliberately switched to. Treats an
    /// untracked splash as ours, since before the main window exists there
    /// is nothing else it could be covering.
    /// </summary>
    private static bool CoversForegroundWindow()
    {
        var tracked = Volatile.Read(ref _trackedHwnd);
        if (tracked == 0) return true;

        // A tracked window that has not been shown yet is still ours. The
        // splash is handed the window as soon as its geometry is applied,
        // which is well before Activate, and until then the foreground window
        // is whatever launched the app -- so testing the foreground alone
        // would stop re-asserting topmost for the whole of that stretch,
        // exactly while the window behind is about to appear painting black.
        if (IsWindowVisible(tracked) == 0) return true;

        var foreground = GetForegroundWindow();
        return foreground == 0 || foreground == tracked || foreground == _hwnd;
    }

    /// <summary>
    /// Keep the splash glued to the window it is covering. Without this,
    /// moving the window during startup slides it out from under the
    /// splash, leaving the icon floating over the desktop and the black
    /// gap exposed.
    /// </summary>
    private static void FollowTrackedWindow()
    {
        var tracked = Volatile.Read(ref _trackedHwnd);
        if (tracked == 0) return;
        if (GetWindowRect(tracked, out var r) == 0) return;

        var width = r.right - r.left;
        var height = r.bottom - r.top;
        if (width <= 0 || height <= 0) return;

        if (GetWindowRect(_hwnd, out var mine) != 0
            && mine.left == r.left && mine.top == r.top
            && mine.right - mine.left == width && mine.bottom - mine.top == height)
        {
            return;
        }

        var resized = width != _width || height != _height;
        _width = width;
        _height = height;

        SetWindowPos(_hwnd, HWND_TOPMOST, r.left, r.top, width, height, SWP_NOACTIVATE);

        // Belt and braces. A move across a DPI boundary normally arrives as
        // WM_DPICHANGED sent synchronously from inside the SetWindowPos above,
        // so by this line WndProc has usually adopted the new scale already
        // and this call is a no-op. It stays because the whole feature turns
        // on that message: the icon would silently keep the launch monitor's
        // size if it ever went undelivered, and one GetDpiForWindow per moved
        // frame is not a cost worth trading for that.
        var rescaled = AdoptDpi(GetDpiForWindow(_hwnd));

        // A move alone keeps the existing layered bitmap; a resize or a scale
        // change needs a new one, both because the surface is a different size
        // and because the icon rescales with the window.
        if (resized || rescaled) Paint(_alpha);
    }

    /// <summary>
    /// Adopt <paramref name="dpi"/> as the scale the splash draws at.
    /// Returns true when it actually changed, which means the composed
    /// bitmap is stale and the caller owes a repaint.
    /// </summary>
    private static bool AdoptDpi(uint dpi)
    {
        // GetDpiForWindow returns zero for an invalid window. Treating that
        // as 100% keeps a failure looking like an unscaled display rather
        // than collapsing the icon to nothing.
        if (dpi == 0) dpi = 96;
        if (dpi == _dpi) return false;
        _dpi = dpi;
        return true;
    }

    /// <summary>
    /// Fade the splash out. Keeps servicing the message queue and tracking
    /// the window while it does: this is a top-level window, so a thread
    /// that stops pumping stalls any process broadcasting a message to it,
    /// and a drag during the fade would otherwise detach the splash.
    /// </summary>
    private static void FadeOut()
    {
        // HideNow has already taken the window off screen and its caller is
        // ending the process. Fading a hidden window would only keep this
        // thread alive through a teardown that is waiting on it.
        if (Volatile.Read(ref _skipFade)) return;

        var steps = Math.Max(1, FadeDurationMs / FadeStepMs);
        for (var i = steps - 1; i >= 0; i--)
        {
            var alpha = (byte)(255 * i / steps);
            FollowTrackedWindow();
            if (!Paint(alpha)) break;
            PumpMessages();
            Thread.Sleep(FadeStepMs);
        }
    }

    /// <summary>
    /// Show the splash at <paramref name="alpha"/>, composing the bitmap
    /// first if there is not already one at the current size. The content
    /// is fully opaque, so <paramref name="alpha"/> alone drives the fade
    /// and there is no premultiplied alpha to get wrong.
    /// </summary>
    private static bool Paint(byte alpha)
    {
        if (!EnsureSurface()) return false;

        _alpha = alpha;

        var size = new SIZE { cx = _surfaceWidth, cy = _surfaceHeight };
        var srcPoint = new POINT { x = 0, y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0,                      // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = 0,                  // opaque source, constant alpha only
        };

        return UpdateLayeredWindow(
            _hwnd, _surfaceScreenDc, 0, ref size, _surfaceMemDc,
            ref srcPoint, 0, ref blend, ULW_ALPHA) != 0;
    }

    /// <summary>
    /// Compose the splash bitmap into a 32bpp DIB, reusing the existing one
    /// when it already matches the current size. Kept apart from the blend
    /// because composing costs a full-window allocation, a per-pixel fill
    /// and a PNG decode, and the fade changes only the alpha: rebuilding it
    /// per frame turned a 220ms fade into a stutter that delayed the very
    /// reveal it was smoothing.
    /// </summary>
    private static bool EnsureSurface()
    {
        var width = _width;
        var height = _height;
        if (width <= 0 || height <= 0) return false;

        // The DPI is part of the key, not just the size: the icon is sized
        // from it, so a same-size move onto a monitor at a different scale
        // leaves a bitmap that is the right shape and the wrong drawing.
        if (_surfaceDib != 0 && _surfaceWidth == width && _surfaceHeight == height
            && _surfaceDpi == _dpi)
        {
            return true;
        }

        ReleaseSurface();

        var background = _background;
        var dpi = _dpi;
        var scale = dpi / 96.0;
        var iconPx = (int)Math.Round(
            LaunchIconMetrics.Resolve(width / scale, height / scale) * scale);

        var screenDc = GetDC(0);
        if (screenDc == 0)
        {
            Diag("GetDC failed; no splash this launch");
            return false;
        }

        var memDc = CreateCompatibleDC(screenDc);
        if (memDc == 0)
        {
            Diag("CreateCompatibleDC failed; no splash this launch");
            ReleaseDC(0, screenDc);
            return false;
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            // Negative height means top-down, so row 0 is the top row and
            // the GDI+ draw below lands the right way up.
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var dib = CreateDIBSection(memDc, ref header, 0, out var bits, 0, 0);
        if (dib == 0)
        {
            Diag($"CreateDIBSection failed for {width}x{height}; no splash this launch");
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
            return false;
        }

        var oldBitmap = SelectObject(memDc, dib);

        // Publish all of it or none of it. ReleaseSurface only frees what
        // these fields point at, so committing the bitmap handle before the
        // compose below could leak both DCs and the DIB if anything in it
        // threw.
        _surfaceScreenDc = screenDc;
        _surfaceMemDc = memDc;
        _surfaceDib = dib;
        _surfaceOldBitmap = oldBitmap;
        _surfaceWidth = width;
        _surfaceHeight = height;
        _surfaceDpi = dpi;

        FillOpaque(bits, width, height, background);
        DrawIcon(memDc, width, height, iconPx);
        return true;
    }

    /// <summary>
    /// Drop the composed bitmap and every GDI object behind it. Safe to
    /// call when there is nothing to release.
    /// </summary>
    private static void ReleaseSurface()
    {
        if (_surfaceMemDc != 0)
        {
            if (_surfaceOldBitmap != 0) SelectObject(_surfaceMemDc, _surfaceOldBitmap);
            DeleteDC(_surfaceMemDc);
        }
        if (_surfaceDib != 0) DeleteObject(_surfaceDib);
        if (_surfaceScreenDc != 0) ReleaseDC(0, _surfaceScreenDc);

        _surfaceScreenDc = 0;
        _surfaceMemDc = 0;
        _surfaceDib = 0;
        _surfaceOldBitmap = 0;
        _surfaceWidth = 0;
        _surfaceHeight = 0;
        _surfaceDpi = 0;
    }

    /// <summary>
    /// Fill the DIB with the background colour at full alpha. Written
    /// directly rather than with FillRect because a GDI brush fill leaves
    /// the alpha channel at zero, which UpdateLayeredWindow then treats
    /// as fully transparent -- an invisible splash.
    /// </summary>
    private static void FillOpaque(nint bits, int width, int height, uint rgb)
    {
        var pixel = 0xFF000000u | (rgb & 0x00FFFFFFu);
        var p = (uint*)bits;
        var count = (long)width * height;
        for (long i = 0; i < count; i++) p[i] = pixel;
    }

    private static void DrawIcon(nint memDc, int width, int height, int iconPx)
    {
        var path = IconPathForSize(iconPx);
        if (path is null)
        {
            // Without this the splash still paints, as a bare rectangle in
            // the terminal background colour, which reads as a rendering
            // bug rather than as missing assets.
            Diag($"no SplashIcon asset for {iconPx}px in {AppContext.BaseDirectory}Assets");
            return;
        }

        var startup = new GdiplusStartupInput { GdiplusVersion = 1 };
        if (GdiplusStartup(out var token, ref startup, 0) != 0)
        {
            Diag("GdiplusStartup failed; drawing without the icon");
            return;
        }

        try
        {
            nint image;
            fixed (char* file = path)
            {
                if (GdipCreateBitmapFromFile(file, out image) != 0 || image == 0)
                {
                    // Present but unreadable: truncated, corrupt, or locked.
                    // Same bare-rectangle result as a missing file, so it
                    // needs the same signal.
                    Diag($"could not decode {path}; drawing without the icon");
                    return;
                }
            }

            try
            {
                if (GdipCreateFromHDC(memDc, out var graphics) != 0 || graphics == 0)
                {
                    Diag("GdipCreateFromHDC failed; drawing without the icon");
                    return;
                }
                try
                {
                    GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic);
                    GdipSetPixelOffsetMode(graphics, PixelOffsetModeHighQuality);
                    GdipDrawImageRectI(
                        graphics, image,
                        (width - iconPx) / 2, (height - iconPx) / 2, iconPx, iconPx);
                }
                finally { GdipDeleteGraphics(graphics); }
            }
            finally { GdipDisposeImage(image); }
        }
        finally { GdiplusShutdown(token); }
    }

    /// <summary>
    /// Pick the smallest shipped rung at least as large as the size being
    /// drawn, so GDI+ downsamples rather than upsamples. Falls back to the
    /// largest rung when the request exceeds everything we ship.
    /// </summary>
    private static string? IconPathForSize(int iconPx)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets");

        // Pixel size of each rung paired with the scale suffix it ships
        // under. Keep in sync with PngWriter.SplashTargets.
        ReadOnlySpan<int> rungPixels = [160, 240, 320, 640];
        ReadOnlySpan<int> rungScales = [100, 150, 200, 400];

        for (var i = 0; i < rungPixels.Length; i++)
        {
            if (rungPixels[i] < iconPx) continue;
            var candidate = Path.Combine(dir, $"SplashIcon.scale-{rungScales[i]}.png");
            if (File.Exists(candidate)) return candidate;
        }

        var largest = Path.Combine(dir, "SplashIcon.scale-400.png");
        return File.Exists(largest) ? largest : null;
    }

    /// <summary>
    /// Read the saved window state once, or null if there is none to read.
    /// Loaded once and shared: each call is a directory create plus a file
    /// read plus a deserialize, on the path whose whole purpose is to get
    /// something on screen quickly, and two reads can disagree because the
    /// main window rewrites this file while it is starting up.
    /// </summary>
    private static Ghostty.Settings.WindowState? LoadState()
    {
        try
        {
            return Ghostty.Settings.WindowState.Load();
        }
        catch (Exception ex)
        {
            Diag($"could not read the saved window state: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Where the main window is about to appear, in physical pixels.
    /// </summary>
    /// <remarks>
    /// <para>Two sources, most specific first. A restored session places its
    /// first window from that session's saved geometry; every other launch
    /// places its window from window-state.json. Reading only the latter is
    /// wrong for any multi-window session, because window-state.json is
    /// written by whichever window closed last while the window being
    /// covered is the first one restored -- so the splash would sit on a
    /// different window's rect for exactly the gap it exists to cover.</para>
    ///
    /// <para>A best guess rather than a mirror of the window's own decision.
    /// The window consults exactly one of the two, and when it rejects that
    /// one it lets the OS place it instead of trying the other. The guess
    /// only has to hold until <see cref="Track"/> hands over the real rect.</para>
    ///
    /// <para>Both sources go through <c>WindowGeometryGate</c>, which is the
    /// same size and position check <c>MainWindow.ApplyGeometry</c> applies:
    /// a rect the window would reject is one the splash must reject too. The
    /// on-screen test is not shared, because the window's uses WinUI's
    /// DisplayArea and nothing WinUI exists yet on this thread.</para>
    /// </remarks>
    private static (int X, int Y, int Width, int Height) ResolveRect(
        Ghostty.Settings.WindowState? state)
    {
        if (TryResolveGeometry(LoadSessionGeometry(), out var rect)) return rect;
        if (TryResolveGeometry(ToGeometry(state), out rect)) return rect;

        var screenWidth = GetSystemMetrics(SM_CXSCREEN);
        var screenHeight = GetSystemMetrics(SM_CYSCREEN);
        var width = Math.Min(FallbackWidth, screenWidth);
        var height = Math.Min(FallbackHeight, screenHeight);
        return ((screenWidth - width) / 2, (screenHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Turn a saved geometry into the physical rect the window will occupy,
    /// or fail when the window itself would refuse to use it.
    /// </summary>
    private static bool TryResolveGeometry(
        Ghostty.Core.Session.WindowGeometry? geometry,
        out (int X, int Y, int Width, int Height) rect)
    {
        rect = default;
        if (geometry is null) return false;
        if (!Ghostty.Core.Session.WindowGeometryGate.TryNormalize(geometry, out var r))
        {
            return false;
        }

        var (x, y, w, h) = r;
        if (!IntersectsLiveMonitor(x, y, w, h)) return false;

        // A maximized window comes up filling its monitor's work area, not
        // the restored rect that was saved alongside the flag.
        if (geometry.Maximized && TryGetWorkArea(x, y, w, h, out var work))
        {
            rect = work;
            return true;
        }

        rect = r;
        return true;
    }

    /// <summary>
    /// The window-state.json placement in the shape ApplyGeometry consumes.
    /// Mirrors <c>MainWindow.RestoreWindowPlacement</c>, which builds the
    /// same geometry from the same fields for the non-restore path.
    /// </summary>
    private static Ghostty.Core.Session.WindowGeometry? ToGeometry(
        Ghostty.Settings.WindowState? state) =>
        state is null ? null : new Ghostty.Core.Session.WindowGeometry
        {
            X = state.WindowX,
            Y = state.WindowY,
            Width = state.WindowWidth,
            Height = state.WindowHeight,
            Maximized = state.WindowMaximized,
        };

    /// <summary>
    /// Geometry of the first window a session restore would rebuild, or null
    /// when there is no session to restore from.
    /// </summary>
    /// <remarks>
    /// <para>Approximates <c>SessionManager.LoadForRestore</c>. The real gate
    /// also consults <c>window-save-state</c>, which lives in the Wintty
    /// config and so behind a libghostty load -- seconds of work on the one
    /// path whose whole job is to be on screen first. The clean-shutdown flag
    /// is in the session file itself and is what the default policy turns on,
    /// so it is read here and the config key is not.</para>
    ///
    /// <para>That buys the common case at the cost of two rare ones, both of
    /// which end with the splash on a rect the window does not use:
    /// <c>always</c> after an unclean exit, where the window restores and
    /// this declines; and the single launch after a switch to <c>never</c>,
    /// where a stale clean file is still on disk, this honours it and the
    /// window discards it. Neither is silent for long -- <see cref="Track"/>
    /// hands over the real rect as soon as the window has one.</para>
    /// </remarks>
    private static Ghostty.Core.Session.WindowGeometry? LoadSessionGeometry()
    {
        try
        {
            var session = Ghostty.Session.SessionStore.ReadFile();
            if (session is null || !session.CleanShutdown) return null;
            if (session.Windows.Count == 0) return null;

            // A window with no tabs is one the restore will not rebuild, and
            // MainWindow then falls back to window-state.json for its
            // placement. Following it here would reintroduce the very
            // mismatch this method exists to remove.
            var first = session.Windows[0];
            return first.Tabs.Count > 0 ? first.Geometry : null;
        }
        catch (Exception ex)
        {
            Diag($"could not read the saved session: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when any part of the rect lands on a live monitor. Guards
    /// against a saved position on a display that is no longer attached.
    /// Asks the window manager rather than testing the virtual-screen
    /// bounding box, which on an L-shaped arrangement contains dead space
    /// that belongs to no monitor.
    /// </summary>
    private static bool IntersectsLiveMonitor(int x, int y, int w, int h)
    {
        // Grown by a pixel on every side before the test. MonitorFromRect
        // wants a real overlap, so a rect whose right edge is exactly a
        // monitor's left edge reads as off-screen -- while ApplyGeometry,
        // whose bounds test is a strict inequality, would still place the
        // window there. This only reconciles that touching-edge case; the two
        // tests are not otherwise equivalent, since ApplyGeometry measures
        // against one nearest display's work area and this measures against
        // the bounds of every live monitor.
        var rect = new RECT
        {
            left = x - 1,
            top = y - 1,
            right = x + w + 1,
            bottom = y + h + 1,
        };
        return MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) != 0;
    }

    /// <summary>
    /// Work area of the monitor holding the centre of the given rect.
    /// </summary>
    private static bool TryGetWorkArea(
        int x, int y, int w, int h, out (int X, int Y, int Width, int Height) area)
    {
        area = default;

        var centre = new POINT { x = x + (w / 2), y = y + (h / 2) };
        var monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return false;

        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (GetMonitorInfoW(monitor, ref info) == 0) return false;

        // A maximized window's rect is the work area grown by the invisible
        // resize border, not the work area itself. Matching it exactly keeps
        // a black frame from showing around the splash for the whole gap.
        var border = GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
        var left = info.rcWork.left - border;
        var top = info.rcWork.top - border;
        var width = (info.rcWork.right - info.rcWork.left) + (border * 2);
        var height = (info.rcWork.bottom - info.rcWork.top) + (border * 2);
        if (width <= 0 || height <= 0) return false;

        area = (left, top, width, height);
        return true;
    }

    private static uint ResolveBackgroundRgb(Ghostty.Settings.WindowState? state)
    {
        if (state?.BackgroundRgb is uint saved) return saved & 0x00FFFFFFu;
        return DefaultBackgroundRgb;
    }

    private static bool RegisterWindowClass()
    {
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = GetModuleHandleW(0),
                // A class with no cursor leaves whatever the OS was last
                // showing in place, which during process start is the
                // app-starting hourglass. WS_EX_TRANSPARENT means
                // hit-testing should never reach us, so this is belt and
                // braces -- but a null class cursor is a latent defect
                // rather than a deliberate choice.
                hCursor = LoadCursorW(0, IDC_ARROW),
                lpszClassName = name,
            };
            // Show() guarantees one registration per process, so a zero
            // atom here is a genuine failure rather than "already
            // registered".
            return RegisterClassExW(ref wc) != 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // How the splash learns its scale changed under it: the user drags
        // the covered window onto a monitor at a different scale, or edits
        // the display's scale factor in Settings while the splash is up.
        // DefWindowProc does nothing with this for a layered window, so
        // without the case the icon keeps the launch monitor's physical size
        // for the rest of the splash -- half or double what it should be.
        //
        // The DPI comes from wParam because that is what the message carries
        // and it is authoritative for the move that raised it. The suggested
        // rect in lParam is ignored: that is advice for a window that owns
        // its placement, and this one takes its rect from the window it
        // covers.
        //
        // Guarded on the handle because the splash is a singleton addressed
        // through a static. WndProc runs for messages sent during
        // CreateWindowExW, before _hwnd is assigned, and for messages
        // dispatched by DestroyWindow, after it is cleared; neither should
        // touch a surface.
        if (msg == WM_DPICHANGED && hwnd == _hwnd)
        {
            if (AdoptDpi((uint)wParam & 0xFFFF)) Paint(_alpha);
            return 0;
        }

        // Layered content is supplied wholesale by UpdateLayeredWindow, so
        // there is no WM_PAINT work and no background to erase.
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const int SW_SHOWNA = 8;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint ULW_ALPHA = 0x00000002;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;
    private const uint MONITOR_DEFAULTTONULL = 0x00000000;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint IDC_ARROW = 32512;
    private const int InterpolationModeHighQualityBicubic = 7;
    private const int PixelOffsetModeHighQuality = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int cmdShow);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref MSG msg);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint cursorName);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref RECT rect, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial int UpdateLayeredWindow(
        nint hwnd, nint destDc, nint destPoint, ref SIZE size,
        nint srcDc, ref POINT srcPoint, uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(
        nint dc, ref BITMAPINFOHEADER header, uint usage,
        out nint bits, nint section, uint offset);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdiplusStartup(out nint token, ref GdiplusStartupInput input, nint output);

    [LibraryImport("gdiplus.dll")]
    private static partial void GdiplusShutdown(nint token);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateBitmapFromFile(char* filename, out nint bitmap);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDisposeImage(nint image);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateFromHDC(nint dc, out nint graphics);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetInterpolationMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDrawImageRectI(
        nint graphics, nint image, int x, int y, int width, int height);
}
