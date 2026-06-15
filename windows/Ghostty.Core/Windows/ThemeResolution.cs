using System;

namespace Ghostty.Core.Windows;

/// <summary>
/// How the resolver interprets <c>window-theme</c> values that are not
/// explicitly <c>light</c>, <c>dark</c>, or <c>system</c> (i.e.
/// <c>auto</c>, <c>ghostty</c>, and unknown values). The terminal
/// chrome and the command palette use Palette so they track the active
/// colour palette and match each other (#236); the Settings surface uses
/// System so it feels OS-native regardless of the terminal's colours.
/// </summary>
public enum ThemeFallbackStyle
{
    Palette,
    System,
}

/// <summary>
/// Pure resolution of the libghostty <c>window-theme</c> config value
/// to a dark/light boolean. No WinUI, no Win32, no ambient state —
/// every input is passed in, so the function is trivially unit-testable
/// and stays in <c>Ghostty.Core</c>.
///
/// Callers (e.g. <c>Ghostty.Services.WindowThemeManager</c>) own the
/// subscriptions to <c>IConfigService</c> and <c>UISettings</c> and
/// feed the current values into <see cref="ResolveIsDark"/>. They also
/// use <see cref="TracksSystem"/> to decide whether a system-theme
/// flip should trigger a re-resolve.
/// </summary>
public static class ThemeResolution
{
    /// <summary>
    /// Resolve a <c>window-theme</c> value to a dark-mode boolean.
    /// </summary>
    /// <param name="windowTheme">Config value. Recognised: "light",
    /// "dark", "system". Anything else (including null/empty) consults
    /// <paramref name="fallback"/>.</param>
    /// <param name="backgroundColor">Terminal background colour packed
    /// as <c>0x00RRGGBB</c>. Only used when <paramref name="fallback"/>
    /// is <see cref="ThemeFallbackStyle.Palette"/>.</param>
    /// <param name="fallback">Behaviour for auto/ghostty/unknown values.</param>
    /// <param name="isSystemDark">Current OS dark-mode state. Used when
    /// <paramref name="windowTheme"/> is "system" or when
    /// <paramref name="fallback"/> is
    /// <see cref="ThemeFallbackStyle.System"/>.</param>
    public static bool ResolveIsDark(
        string windowTheme,
        uint backgroundColor,
        ThemeFallbackStyle fallback,
        bool isSystemDark) => windowTheme switch
    {
        "light" => false,
        "dark" => true,
        "system" => isSystemDark,
        _ => fallback == ThemeFallbackStyle.System
            ? isSystemDark
            : IsBackgroundDark(backgroundColor),
    };

    /// <summary>
    /// True when the resolved value depends on the OS theme. Callers
    /// use this to skip dispatching work on <c>ColorValuesChanged</c>
    /// when the system theme cannot affect the outcome.
    /// </summary>
    public static bool TracksSystem(
        string windowTheme,
        ThemeFallbackStyle fallback) => windowTheme switch
    {
        "light" or "dark" => false,
        "system" => true,
        _ => fallback == ThemeFallbackStyle.System,
    };

    /// <summary>
    /// BT.709 relative-luminance test: a colour is "dark" when luminance
    /// is below 0.5. Matches the macOS port's <c>NSColor.isLightColor</c>
    /// heuristic upstream, so Windows and macOS agree on auto-theme
    /// decisions for a given palette.
    /// </summary>
    public static bool IsBackgroundDark(uint color)
    {
        var r = (color >> 16) & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = color & 0xFF;
        var luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        return luminance < 0.5;
    }

    /// <summary>
    /// Whether a light (white) foreground reads better than a dark
    /// (black) one over the given backdrop. Thin intent-revealing alias
    /// over <see cref="IsBackgroundDark"/> so caption-button and tab-text
    /// call sites read as "pick a readable foreground for this backdrop"
    /// while the luminance math stays in one place. (#235, #342)
    /// </summary>
    public static bool PreferLightForeground(uint backgroundColor) =>
        IsBackgroundDark(backgroundColor);

    /// <summary>
    /// Relative luminance of an sRGB colour per WCAG 2.x, with each channel
    /// gamma-expanded to linear light. 0.0 for black, 1.0 for white. Kept
    /// separate from <see cref="IsBackgroundDark"/>'s cheap BT.709 estimate
    /// because contrast ratios need the linearised value to match what the
    /// eye perceives across the whole range.
    /// </summary>
    private static double RelativeLuminance(uint color)
    {
        static double Linearize(uint channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var r = Linearize((color >> 16) & 0xFF);
        var g = Linearize((color >> 8) & 0xFF);
        var b = Linearize(color & 0xFF);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>
    /// WCAG contrast ratio between two sRGB colours packed as 0x00RRGGBB.
    /// Ranges from 1.0 (identical luminance) to 21.0 (pure black against
    /// pure white). Order-independent.
    /// </summary>
    public static double ContrastRatio(uint a, uint b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Pick a legible foreground for text drawn over
    /// <paramref name="background"/>. Keeps <paramref name="desired"/> when it
    /// already clears the WCAG AA contrast threshold (4.5:1); otherwise falls
    /// back to pure white or black, whichever reads better over the backdrop.
    /// Both arguments and the result are packed 0x00RRGGBB.
    ///
    /// This is what keeps the active tab title readable regardless of palette:
    /// the selected-tab background is the accent (cursor-colour by default,
    /// which is light), so an inherited white title would vanish — this maps
    /// it to black. It also guards the shell-theme path, where accent and
    /// cursor-text can both land light or both dark (#342).
    /// </summary>
    public static uint EnsureReadableForeground(
        uint background, uint desired, double minContrast = 4.5)
    {
        if (ContrastRatio(background, desired) >= minContrast)
            return desired;
        return PreferLightForeground(background) ? 0xFFFFFFu : 0x000000u;
    }
}
