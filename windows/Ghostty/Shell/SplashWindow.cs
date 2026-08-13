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

    private const int FadeDurationMs = 220;
    private const int FadeStepMs = 16;

    // Hard stop. If the main window never reports content the splash must
    // still go away rather than sit on top of the user's desktop forever.
    private const int WatchdogMs = 10_000;

    private const string ClassName = "WinttySplash";

    private static nint _hwnd;
    private static readonly ManualResetEventSlim _dismissed = new(false);
    private static int _started;
    private static long _shownAtTicks;

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
        if (Volatile.Read(ref _started) == 0) return;
        _dismissed.Set();
    }

    private static void ThreadMain()
    {
        try
        {
            RunSplash();
        }
        catch
        {
            // A splash is decoration. Nothing it can do is worth taking the
            // process down before the real window exists, and there is no
            // logger wired up this early in startup.
        }
    }

    private static void RunSplash()
    {
        var (x, y, width, height) = ResolveRect();
        var background = ResolveBackgroundRgb();

        if (!RegisterWindowClass()) return;

        fixed (char* className = ClassName)
        {
            _hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                className, null, WS_POPUP,
                x, y, width, height,
                0, 0, GetModuleHandleW(0), 0);
        }
        if (_hwnd == 0) return;

        try
        {
            // DPI is only knowable once the window exists and the OS has
            // assigned it to a monitor.
            var dpi = GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            var scale = dpi / 96.0;
            var iconPx = (int)Math.Round(
                LaunchIconMetrics.Resolve(width / scale, height / scale) * scale);

            if (!Paint(width, height, background, iconPx, 255)) return;
            ShowWindow(_hwnd, SW_SHOWNA);
            Volatile.Write(ref _shownAtTicks, Environment.TickCount64);

            PumpUntilDismissed();
            FadeOut(width, height, background, iconPx);
        }
        finally
        {
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
        while (!_dismissed.IsSet && Environment.TickCount64 < deadline)
        {
            // Re-assert topmost. Creating the window with WS_EX_TOPMOST is
            // not enough: when WinUI shows and activates the main window,
            // the splash ends up behind it and the black gap it is meant
            // to be covering shows through. Nudging the z-order on every
            // tick is cheap and keeps the splash on top for the handful of
            // seconds it exists. SWP_NOACTIVATE so this never steals focus
            // from the window coming up behind it.
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE) != 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            // Waits on the dismiss signal rather than sleeping blindly, so
            // the fade starts the moment the window reports content.
            _dismissed.Wait(FadeStepMs);
        }
    }

    private static void FadeOut(int width, int height, uint background, int iconPx)
    {
        var steps = Math.Max(1, FadeDurationMs / FadeStepMs);
        for (var i = steps - 1; i >= 0; i--)
        {
            var alpha = (byte)(255 * i / steps);
            if (!Paint(width, height, background, iconPx, alpha)) break;
            Thread.Sleep(FadeStepMs);
        }
    }

    /// <summary>
    /// Render the splash into a 32bpp DIB and hand it to
    /// UpdateLayeredWindow. The content is fully opaque, so
    /// <paramref name="alpha"/> alone drives the fade and there is no
    /// premultiplied alpha to get wrong.
    /// </summary>
    private static bool Paint(int width, int height, uint background, int iconPx, byte alpha)
    {
        var screenDc = GetDC(0);
        if (screenDc == 0) return false;

        var memDc = CreateCompatibleDC(screenDc);
        if (memDc == 0)
        {
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
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
            return false;
        }

        var oldBitmap = SelectObject(memDc, dib);
        FillOpaque(bits, width, height, background);
        DrawIcon(memDc, width, height, iconPx);

        var size = new SIZE { cx = width, cy = height };
        var srcPoint = new POINT { x = 0, y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0,                      // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = 0,                  // opaque source, constant alpha only
        };

        var ok = UpdateLayeredWindow(
            _hwnd, screenDc, 0, ref size, memDc, ref srcPoint, 0, ref blend, ULW_ALPHA) != 0;

        SelectObject(memDc, oldBitmap);
        DeleteObject(dib);
        DeleteDC(memDc);
        ReleaseDC(0, screenDc);
        return ok;
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
        if (path is null) return;

        var startup = new GdiplusStartupInput { GdiplusVersion = 1 };
        if (GdiplusStartup(out var token, ref startup, 0) != 0) return;

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
    /// Where the main window is about to appear, in physical pixels. Read
    /// straight from the saved window state because that is what the
    /// window itself restores from; anything else would put the splash
    /// somewhere the black gap is not.
    /// </summary>
    private static (int X, int Y, int Width, int Height) ResolveRect()
    {
        try
        {
            var state = Ghostty.Settings.WindowState.Load();
            if (state.WindowWidth is int w and > 0
                && state.WindowHeight is int h and > 0
                && state.WindowX is int x
                && state.WindowY is int y)
            {
                return (x, y, w, h);
            }
        }
        catch
        {
            // Unreadable state file. Fall through to the centred default.
        }

        var screenWidth = GetSystemMetrics(SM_CXSCREEN);
        var screenHeight = GetSystemMetrics(SM_CYSCREEN);
        var width = Math.Min(FallbackWidth, screenWidth);
        var height = Math.Min(FallbackHeight, screenHeight);
        return ((screenWidth - width) / 2, (screenHeight - height) / 2, width, height);
    }

    private static uint ResolveBackgroundRgb()
    {
        try
        {
            if (Ghostty.Settings.WindowState.Load().BackgroundRgb is uint saved)
                return saved & 0x00FFFFFFu;
        }
        catch
        {
            // Unreadable state file. Fall through to the default.
        }
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
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const int SW_SHOWNA = 8;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_DESTROY = 0x0002;
    private const uint ULW_ALPHA = 0x00000002;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
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
    private struct POINT { public int x; public int y; }

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
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

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
