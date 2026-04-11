using Microsoft.UI.Xaml;

namespace Ghostty.Branding;

/// <summary>
/// Resolves the owning Window for a live XAML element. Used by
/// AppIconBadge so the click handler can hand the Window to the
/// system-menu interop helper.
///
/// This is a single-root resolver: it returns <see cref="App.RootWindow"/>
/// (the one main window Ghostty currently creates) after verifying the
/// element is actually attached to a XamlRoot. WinUI 3 desktop does not
/// expose Window.Current and FrameworkElement does not surface its owning
/// Window, so this is the simplest reliable path today.
///
/// TODO(multi-window, #161): when the shell supports multiple top-level
/// windows (Move Tab to New Window, Settings window, etc.), replace this
/// with an XamlRoot -> Window registry maintained by App so the badge
/// routes system-menu clicks to the correct window instead of always
/// the main one.
/// </summary>
internal static class WindowHelper
{
    public static Window? GetWindow(FrameworkElement element)
    {
        if (element.XamlRoot is null) return null;
        return App.RootWindow;
    }
}
