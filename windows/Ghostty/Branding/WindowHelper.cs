using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

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
    /// Stamp the brand .ico (wintty.ico) into <paramref name="window"/>'s
    /// AppWindow icon slots so the taskbar group, thumbnail preview,
    /// alt-tab list, and (when WinUI 3 renders one) the system title-bar
    /// show the brand. ApplicationIcon embeds the same .ico as the exe
    /// resource but does NOT wire those runtime slots; without this call
    /// they fall back to the default WinUI 3 icon.
    /// </summary>
    public static void TryApplyAppIcon(Window window)
        => TryApplyIcon(window, "wintty.ico");

    /// <summary>
    /// Settings-window variant: stamps the gear .ico
    /// (wintty-settings.ico) so the OS slots visually match
    /// SettingsWindow.xaml's TitleBar.IconSource and are
    /// distinguishable from terminal windows in alt-tab.
    /// </summary>
    public static void TryApplySettingsIcon(Window window)
        => TryApplyIcon(window, "wintty-settings.ico");

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
}
