using Windows.UI.ViewManagement;

namespace Ghostty.Services;

/// <summary>
/// Detect the current OS light/dark mode. Centralised here so the
/// "which byte of which color means dark" rule can't drift between
/// callers -- <see cref="WindowThemeManager"/> and
/// <see cref="ConfigService"/> historically had two copies.
/// </summary>
internal static class OsTheme
{
    /// <summary>
    /// True when the OS is currently in dark mode.
    /// UISettings.Foreground is white (R greater than 128) in dark
    /// mode (light text on dark background) and black in light mode.
    /// </summary>
    public static bool IsDark()
    {
        var ui = new UISettings();
        var fg = ui.GetColorValue(UIColorType.Foreground);
        return fg.R > 128;
    }
}
