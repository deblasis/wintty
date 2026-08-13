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
    /// True when the OS is currently in dark mode. Activates a
    /// <see cref="UISettings"/> to ask; callers holding one already
    /// should use the overload.
    /// </summary>
    public static bool IsDark() => IsDark(new UISettings());

    /// <summary>
    /// True when the given settings snapshot reports dark mode.
    /// UISettings.Foreground is white (R greater than 128) in dark
    /// mode (light text on dark background) and black in light mode.
    /// </summary>
    /// <remarks>
    /// Takes the instance so a ColorValuesChanged handler can classify
    /// the event's own sender rather than activating a second one, which
    /// could answer for a different moment.
    /// </remarks>
    public static bool IsDark(UISettings settings)
        => settings.GetColorValue(UIColorType.Foreground).R > 128;
}
