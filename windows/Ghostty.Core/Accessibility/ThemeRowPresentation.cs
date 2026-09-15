namespace Ghostty.Core.Accessibility;

/// <summary>The mark a theme row in the command palette carries on its right.</summary>
public enum ThemeRowBadge
{
    None,
    /// <summary>The theme the config names now (the half on screen, for a pair).</summary>
    Current,
    /// <summary>The theme the browse is showing, or about to show.</summary>
    Previewing,
}

/// <summary>
/// What a theme row in the command palette says, to the eye and to a screen
/// reader: its accessible name, its help text, the light/dark hint under the
/// name, the badge on its right, and what is spoken when the highlight lands
/// on it.
///
/// Pure so the wording and the precedence (current beats previewing) are
/// tested without a live list; the control only publishes what comes out.
/// </summary>
public static class ThemeRowPresentation
{
    /// <summary>
    /// The row's accessible name: the theme, and whether it is the configured
    /// one. Stable while the highlight moves, so a reader walking the list
    /// hears the same name for a row every time; being highlighted is the
    /// list's selection, which UIA already reports.
    /// </summary>
    public static string Name(string themeName, bool isCurrent)
        => isCurrent ? themeName + ", current theme" : themeName;

    /// <summary>The short hint under the name, or null before the swatch is known.</summary>
    public static string? Hint(bool? isDark) => isDark switch
    {
        true => "Dark",
        false => "Light",
        null => null,
    };

    /// <summary>The row's help text, or null (cleared) before the swatch is known.</summary>
    public static string? HelpText(bool? isDark) => isDark switch
    {
        true => "Dark theme",
        false => "Light theme",
        null => null,
    };

    /// <summary>
    /// The badge. The configured theme is marked as such even while it is the
    /// one previewed (arrowing back to it previews exactly what is configured),
    /// and a row the browse has not previewed carries nothing, which is what
    /// keeps the first highlight of a fresh list from claiming a preview.
    /// </summary>
    public static ThemeRowBadge Badge(bool isCurrent, bool isPreviewed)
        => isCurrent ? ThemeRowBadge.Current
            : isPreviewed ? ThemeRowBadge.Previewing
            : ThemeRowBadge.None;

    /// <summary>
    /// What is spoken when the highlight lands on the row. The palette never
    /// moves focus off its search box, so this announcement is the only way a
    /// screen reader hears which theme the terminals just switched to.
    /// </summary>
    public static string Announcement(string themeName, bool isCurrent, bool isPreviewed, bool? isDark)
    {
        var head = Badge(isCurrent, isPreviewed) switch
        {
            ThemeRowBadge.Current => themeName + ", current theme",
            ThemeRowBadge.Previewing => "Previewing " + themeName,
            _ => themeName,
        };
        return Hint(isDark) is { } hint ? head + ", " + hint.ToLowerInvariant() : head;
    }

    /// <summary>The automation properties the row's container carries.</summary>
    public static CommandRowAutomation Automation(string themeName, bool isCurrent, bool? isDark)
        => new(Name(themeName, isCurrent), HelpText(isDark), null);
}
