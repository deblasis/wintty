using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Ghostty.Branding;

/// <summary>
/// Window-level branding helpers. Two unrelated concerns share this
/// file because both are window-owner plumbing the rest of the shell
/// reaches for:
///
///   - <see cref="GetWindow"/> resolves the owning Window for a live
///     XAML element (used by the AppIconBadge click handler so it can
///     pass the Window to the system-menu interop helper).
///   - <see cref="TryApplyAppIcon"/> wires the deployed wintty.ico
///     into the OS-level window slots (taskbar group, thumbnail-hover
///     preview, alt-tab list, title-bar icon when WinUI 3 renders one).
/// </summary>
internal static class WindowHelper
{
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
    /// Apply the deployed wintty.ico to <paramref name="window"/>'s
    /// AppWindow so the OS renders the brand in the taskbar group,
    /// the thumbnail-hover preview, the alt-tab list, and the
    /// system title-bar slot (when WinUI 3 renders one). The .ico is
    /// produced at build time by IconGen and copied into the
    /// <c>Assets</c> directory next to the exe by Ghostty.csproj's
    /// Content item group; the path here mirrors that Link target.
    ///
    /// ApplicationIcon already embeds the same .ico as the exe
    /// resource (which Explorer and the cold-start fallback path
    /// use), but AppWindow.SetIcon needs a real file path on disk
    /// to wire the runtime window-icon slots. Without this call,
    /// taskbar and alt-tab fall back to the default WinUI 3 icon
    /// instead of the embedded resource.
    ///
    /// Swallows the IO + COM failure modes that AppWindow.SetIcon
    /// can throw (missing file, locked file, transient COM hiccup):
    /// a missing window icon is ugly but non-fatal, so dropping to
    /// the default beats crashing the window.
    /// </summary>
    public static void TryApplyAppIcon(Window window)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var iconPath = Path.Combine(appDir, "Assets", "wintty.ico");
            if (!File.Exists(iconPath)) return;
            window.AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or COMException)
        {
            Debug.WriteLine(
                $"AppWindow.SetIcon failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
