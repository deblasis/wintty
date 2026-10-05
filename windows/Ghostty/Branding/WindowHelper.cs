using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Ghostty.Core.Windows;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Ghostty.Branding;

/// <summary>
/// Window-owner helpers used by the WinUI 3 shell.
/// </summary>
internal static class WindowHelper
{
    /// <summary>
    /// Resolves the owning Window for a live XAML element. Used by
    /// AppIconBadge so the click handler can hand the Window to the
    /// system-menu interop helper.
    ///
    /// Multi-window aware: looks up the element's XamlRoot in
    /// <see cref="App.WindowsByRoot"/> to find the correct owning window.
    /// Falls back to the first window in the registry if the XamlRoot
    /// lookup misses (e.g. element not yet loaded).
    /// </summary>
    public static Window? GetWindow(FrameworkElement element)
    {
        if (element.XamlRoot is { } root &&
            App.WindowsByRoot.TryGetValue(root, out var window))
            return window;

        // Fallback: return the first window if available.
        foreach (var w in App.AllWindows)
            return w;

        return null;
    }

    /// <summary>
    /// Stamp the brand .ico into the AppWindow icon slots (taskbar
    /// group, thumbnail preview, alt-tab list, system title-bar when
    /// WinUI 3 renders one). ApplicationIcon embeds the same .ico as
    /// the exe resource but does NOT wire those runtime slots.
    /// </summary>
    public static void TryApplyAppIcon(Window window)
        => TryApplyIcon(window, "wintty.ico");

    /// <summary>
    /// Settings-window variant. Uses the gear .ico so the OS slots
    /// visually match SettingsWindow.xaml's TitleBar.IconSource and
    /// the window is distinguishable from terminal windows in alt-tab.
    /// </summary>
    public static void TryApplySettingsIcon(Window window)
        => TryApplyIcon(window, "wintty-settings.ico");

    /// <summary>
    /// Inspector-window variant. Uses the bug .ico so the taskbar /
    /// alt-tab icon matches the command palette's "Toggle Inspector"
    /// glyph and the window is distinguishable from terminal windows.
    /// </summary>
    public static void TryApplyInspectorIcon(Window window)
        => TryApplyIcon(window, "wintty-inspector.ico");

    /// <summary>
    /// Swallows the file-not-found race (asset deleted between the
    /// File.Exists check and the SetIcon call) and the native HRESULT
    /// path. A missing window icon is cosmetic, not crash-worthy.
    /// </summary>
    private static void TryApplyIcon(Window window, string iconFileName)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var iconPath = Path.Combine(appDir, "Assets", iconFileName);
            if (!File.Exists(iconPath)) return;
            window.AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or COMException)
        {
            Debug.WriteLine(
                $"AppWindow.SetIcon failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The work area of the display the CALLER sits on, in physical pixels.
    ///
    /// A window opened on request belongs on the monitor the request came
    /// from. The settings ask arrives as the app-targeted open_config action,
    /// whose event carries no window -- libghostty raises it on the
    /// process-wide bootstrap host, so there is nothing to take an owner
    /// from. The foreground window at the moment the caller runs IS the
    /// requesting one: a keybind and a command-palette entry both fire while
    /// the terminal that owns them still holds the foreground, and the
    /// window about to be opened has not been shown yet, so it cannot be
    /// what this names.
    ///
    /// <paramref name="fallbackWindowId"/> is the caller's own window, used
    /// when there is no foreground window to ask about -- an ask raised from
    /// a window the shell has backgrounded, or a moment with no foreground
    /// at all. Both arms end on
    /// the primary display through DisplayAreaFallback.Primary, which is also
    /// what a window id that has stopped resolving settles on.
    /// </summary>
    public static unsafe WorkAreaRect WorkAreaForCaller(WindowId fallbackWindowId)
    {
        // HWND wraps a raw pointer, so reading the handle out of it takes an
        // unsafe context; the same cast InvisibleCursorFactory makes for a
        // returned handle. Nothing else in here is a pointer.
        var hwnd = (nint)PInvoke.GetForegroundWindow().Value;
        var id = hwnd == 0
            ? fallbackWindowId
            : Win32Interop.GetWindowIdFromWindow(hwnd);
        return WorkAreaOf(DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary));
    }

    /// <summary>
    /// The work area of the display <paramref name="windowId"/> sits on, in
    /// physical pixels, as the plain shape the Core placement math takes.
    ///
    /// DisplayArea is the WinUI 3 equivalent of macOS's NSScreen.mainScreen
    /// and handles multi-monitor correctly; this only converts the
    /// rectangle, so the scaling and clamping rules live in one tested
    /// place instead of once per window constructor.
    /// </summary>
    public static WorkAreaRect WorkAreaFor(WindowId windowId) =>
        WorkAreaOf(DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary));

    /// <summary>
    /// The effective DPI of the monitor that owns <paramref name="workArea"/>,
    /// or 0 when it cannot be read (the placement math treats 0 as unscaled).
    /// The DPI belongs to the TARGET display, not to any window: a window that
    /// has not been shown yet reports the DPI of wherever it was created,
    /// which on a mixed-DPI setup is the primary monitor even though the
    /// window is about to be placed on another one - sizing it for the wrong
    /// screen. Ask the work area, because that is where the window goes.
    /// </summary>
    public static unsafe uint DpiForWorkArea(WorkAreaRect workArea)
    {
        // CsWin32 maps POINT to System.Drawing.Point in this project, the
        // same shape GetCursorPos hands out in QuickTerminalMonitorResolver.
        var centre = new System.Drawing.Point(
            workArea.X + workArea.Width / 2,
            workArea.Y + workArea.Height / 2);
        var monitor = PInvoke.MonitorFromPoint(
            centre, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor.Value == null) return 0;
        uint dpiX = 0, dpiY = 0;
        return PInvoke.GetDpiForMonitor(
            monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY).Failed
            ? 0
            : dpiX;
    }

    private static WorkAreaRect WorkAreaOf(DisplayArea display)
    {
        var work = display.WorkArea;
        return new WorkAreaRect(work.X, work.Y, work.Width, work.Height);
    }
}
