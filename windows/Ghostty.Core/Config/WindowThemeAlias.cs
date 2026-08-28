using System;

namespace Ghostty.Core.Config;

/// <summary>
/// Whether <c>window-theme</c> asks for palette-hued chrome.
///
/// "wintty" is the preferred spelling and "ghostty" the deprecated alias
/// libghostty still parses and hands back through the C API verbatim. The two
/// have to behave identically, or a config written before the rename quietly
/// loses the chrome it asked for. Three independent implementations of that
/// rule is how it drifts, so this is the only one.
/// </summary>
public static class WindowThemeAlias
{
    /// <summary>
    /// Whether <paramref name="windowTheme"/> is either spelling of the
    /// palette-hued value. Case-insensitive because the value reaches us as
    /// the user typed it and every comparison downstream was already
    /// ordinal-ignore-case; not trimmed, because libghostty parses the key
    /// before handing it back and a value with whitespace in it never
    /// survives that far.
    /// </summary>
    public static bool IsPaletteHued(string? windowTheme) =>
        string.Equals(windowTheme, "wintty", StringComparison.OrdinalIgnoreCase)
        || string.Equals(windowTheme, "ghostty", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The canonical spelling, for UI that has to pick one. Anything that is
    /// not the alias comes back as it arrived, so a caller can go on matching
    /// the rest of the values itself.
    /// </summary>
    public static string Canonicalize(string? windowTheme) =>
        IsPaletteHued(windowTheme) ? "wintty" : windowTheme ?? string.Empty;
}
