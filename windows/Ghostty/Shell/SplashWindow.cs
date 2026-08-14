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
/// <para>It covers the rect the main window is about to restore to (from
/// <see cref="Ghostty.Settings.WindowState"/>) and fills it with the
/// terminal background colour, so the black gap is never visible. When
/// the main window reports content, the splash fades and closes,
/// revealing the real window underneath.</para>
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

    // Smallest saved rect worth believing. Matches MainWindow.ApplyGeometry,
    // which discards anything smaller, so the splash and the window agree on
    // when saved geometry is junk.
    private const int MinPlausibleWidth = 200;
    private const int MinPlausibleHeight = 150;

    private const int FadeDurationMs = 220;
    private const int FadeStepMs = 16;

    // How often to re-assert topmost while waiting. Frequent enough that a
    // main window appearing underneath is covered again within a frame or
    // two, rare enough not to load the window manager during startup.
    private const int TopmostNudgeIntervalMs = 250;

    // Hard stop. If the main window never reports content the splash must
    // still go away rather than sit on top of the user's desktop forever.
    private const int WatchdogMs = 10_000;

    private const string ClassName = "WinttySplash";

    private static nint _hwnd;
    private static readonly ManualResetEventSlim _dismissed = new(false);
    private static int _started;
    private static long _shownAtTicks;
    private static nint _trackedHwnd;

    // Current splash geometry, owned by the splash thread. Mutable
    // because the splash follows the main window, which the user can move
    // or resize while it is still up.
    private static int _width;
    private static int _height;
    private static uint _background;
    private static double _scale = 1.0;

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
    /// Report a splash failure on the only channel that exists this early.
    /// Nothing here is worth failing the launch over, but a splash that
    /// silently does nothing -- or paints a bare rectangle because its
    /// icon is missing -- is otherwise invisible in the field.
    /// </summary>
    private static void Diag(string message) =>
        Program.WriteStartupDiagnostic($"splash: {message}");

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
            var dpi = GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            _scale = dpi / 96.0;

            if (!Paint(255)) return;
            ShowWindow(_hwnd, SW_SHOWNA);
            Volatile.Write(ref _shownAtTicks, Environment.TickCount64);

            PumpUntilDismissed();
            FadeOut();
        }
        finally
        {
            ReleaseSurface();
            DestroyWindow(_hwnd);
            _hwnd = 0;
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
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
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

        SetWindowPos(_hwnd, HWND_TOPMOST, r.left, r.top, width, height,
            SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        // A move alone keeps the existing layered bitmap; a resize needs a
        // new one, both because the surface is a different size and
        // because the icon rescales with the window.
        if (resized) Paint(255);
    }

    /// <summary>
    /// Fade the splash out. Keeps servicing the message queue and tracking
    /// the window while it does: this is a top-level window, so a thread
    /// that stops pumping stalls any process broadcasting a message to it,
    /// and a drag during the fade would otherwise detach the splash.
    /// </summary>
    private static void FadeOut()
    {
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

        if (_surfaceDib != 0 && _surfaceWidth == width && _surfaceHeight == height)
        {
            return true;
        }

        ReleaseSurface();

        var background = _background;
        var iconPx = (int)Math.Round(
            LaunchIconMetrics.Resolve(width / _scale, height / _scale) * _scale);

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

        _surfaceOldBitmap = SelectObject(memDc, dib);
        FillOpaque(bits, width, height, background);
        DrawIcon(memDc, width, height, iconPx);

        _surfaceScreenDc = screenDc;
        _surfaceMemDc = memDc;
        _surfaceDib = dib;
        _surfaceWidth = width;
        _surfaceHeight = height;
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
                if (GdipCreateBitmapFromFile(file, out image) != 0 || image == 0) return;
            }

            try
            {
                if (GdipCreateFromHDC(memDc, out var graphics) != 0 || graphics == 0) return;
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
    /// Applies the same plausibility rules as
    /// <c>MainWindow.ApplyGeometry</c>, which decides where the window
    /// really goes. A saved rect the window would reject is one the splash
    /// must reject too: closing while minimized saves a 160x31 rect at
    /// (-32000,-32000), and honouring that puts the splash off-screen for
    /// the whole cold start, which is a silent no-op of the feature.
    /// The window's own check uses WinUI's DisplayArea, which does not
    /// exist yet on this thread, so this uses the virtual screen instead.
    /// </remarks>
    private static (int X, int Y, int Width, int Height) ResolveRect(
        Ghostty.Settings.WindowState? state)
    {
        if (state is not null
            && state.WindowWidth is int w and >= MinPlausibleWidth
            && state.WindowHeight is int h and >= MinPlausibleHeight
            && state.WindowX is int x
            && state.WindowY is int y
            && IntersectsVirtualScreen(x, y, w, h))
        {
            // A maximized window comes up filling its monitor's work area,
            // not the restored rect that was saved alongside the flag.
            if (state.WindowMaximized && TryGetWorkArea(x, y, w, h, out var work))
            {
                return work;
            }
            return (x, y, w, h);
        }

        var screenWidth = GetSystemMetrics(SM_CXSCREEN);
        var screenHeight = GetSystemMetrics(SM_CYSCREEN);
        var width = Math.Min(FallbackWidth, screenWidth);
        var height = Math.Min(FallbackHeight, screenHeight);
        return ((screenWidth - width) / 2, (screenHeight - height) / 2, width, height);
    }

    /// <summary>
    /// True when any part of the rect lands on a live monitor. Guards
    /// against a saved position on a display that is no longer attached.
    /// </summary>
    private static bool IntersectsVirtualScreen(int x, int y, int w, int h)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return true; // Nothing to check against.

        return x < vx + vw && x + w > vx && y < vy + vh && y + h > vy;
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

        var width = info.rcWork.right - info.rcWork.left;
        var height = info.rcWork.bottom - info.rcWork.top;
        if (width <= 0 || height <= 0) return false;

        area = (info.rcWork.left, info.rcWork.top, width, height);
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
    private const uint ULW_ALPHA = 0x00000002;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Post the z-order change instead of waiting on it. SetWindowPos
    // otherwise notifies other top-level windows synchronously, and the
    // thread it would be waiting on is the UI thread that is busy doing
    // the startup work this splash exists to cover.
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
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
    private static partial nint MonitorFromPoint(POINT pt, uint flags);

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
