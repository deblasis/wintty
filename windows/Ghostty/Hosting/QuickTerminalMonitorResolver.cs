using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Hosting;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Hosting;

/// <summary>
/// Adapter that turns a <see cref="QuickTerminalScreen"/> + the
/// quake window's HWND into a <see cref="MonitorBounds"/> the
/// pure-logic resolver consumes. Caller passes the resulting
/// bounds plus the position + size config into
/// <see cref="QuickTerminalGeometry.Resolve"/>.
/// </summary>
internal static class QuickTerminalMonitorResolver
{
    public static MonitorBounds Resolve(IntPtr hwnd, QuickTerminalScreen screen)
    {
        HMONITOR monitor = screen switch
        {
            QuickTerminalScreen.Mouse => ResolveMouseMonitor(),
            _                          => PInvoke.MonitorFromWindow(
                                              new HWND(hwnd),
                                              MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY),
        };
        return ReadBounds(monitor);
    }

    private static HMONITOR ResolveMouseMonitor()
    {
        if (!PInvoke.GetCursorPos(out var pt))
        {
            // GetCursorPos failure is exceptional; fall back to the
            // primary monitor rather than throwing from the toggle
            // path.
            return PInvoke.MonitorFromWindow(
                new HWND(IntPtr.Zero),
                MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        }
        return PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
    }

    private static MonitorBounds ReadBounds(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            // Same fallback as Mouse failure: a full HD primary
            // monitor at the origin. The user sees a wrongly-placed
            // window once instead of a crash.
            return new MonitorBounds(0, 0, 1920, 1080);
        }
        var rc = info.rcWork;
        return new MonitorBounds(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
    }
}
