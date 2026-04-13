using Microsoft.UI.Xaml;

namespace Ghostty.Branding;

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
}
