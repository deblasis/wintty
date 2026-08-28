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
    /// <paramref name="ink"/> laid over <paramref name="ground"/> at
    /// <paramref name="alpha"/>, packed 0x00RRGGBB. Source-over onto an
    /// opaque ground, which is what the compositor leaves on screen when a
    /// translucent brush paints a surface.
    /// </summary>
    public static uint CompositeOver(uint ink, byte alpha, uint ground)
    {
        var r = Blend((ink >> 16) & 0xFF, (ground >> 16) & 0xFF);
        var g = Blend((ink >> 8) & 0xFF, (ground >> 8) & 0xFF);
        var b = Blend(ink & 0xFF, ground & 0xFF);
        return (r << 16) | (g << 8) | b;

        uint Blend(uint over, uint under)
            => ((over * alpha) + (under * (255u - alpha)) + 127u) / 255u;
    }

    /// <summary>
    /// Whether white or black reads better as ink drawn over
    /// <paramref name="background"/> at <paramref name="alpha"/>.
    ///
    /// Scored with <see cref="ContrastRatio"/> on the composited colour, not
    /// with <see cref="PreferLightForeground"/>. That helper answers "is this
    /// surface dark" off a BT.709 luminance split, which is a fair proxy only
    /// for opaque ink: muted ink is a blend of the pole and the ground, so
    /// both candidates are pulled towards the ground and the winner can
    /// change. The two also disagree either side of mid luminance, and mid
    /// luminance is precisely what a translucent frame makes of the chrome.
    /// </summary>
    public static bool PreferLightForegroundAtAlpha(uint background, byte alpha)
        => ContrastRatio(background, CompositeOver(0xFFFFFFu, alpha, background))
            >= ContrastRatio(background, CompositeOver(0x000000u, alpha, background));

    /// <summary>
    /// CIE L* for a relative luminance: 0 for black, 100 for white, spaced
    /// so that equal differences look equal.
    /// </summary>
    private static double Lightness(double luminance)
        => luminance > 0.008856
            ? (116.0 * Math.Cbrt(luminance)) - 16.0
            : 903.3 * luminance;

    /// <summary>
    /// A tint of <paramref name="rgb"/> that sits <paramref name="deltaLStar"/>
    /// away from it perceptually: positive for lighter, negative for darker.
    /// Both argument and result are packed 0x00RRGGBB.
    /// </summary>
    /// <remarks>
    /// <para>Perceptual rather than a step per channel, because those are not
    /// the same thing. sRGB is gamma encoded, so a fixed number of counts
    /// buys markedly more visible separation down at the black end than up
    /// at the white end. Anything tuned to look right on a dark background
    /// and then reused on a light one comes out weaker than intended, which
    /// is how a texture calibrated in dark mode ends up nearly invisible in
    /// light mode.</para>
    ///
    /// <para>Every channel moves by the same number of counts, so the result
    /// normally stays a tint of the input rather than becoming a colour of
    /// its own. Two things stop holding once a channel hits a rail, since a
    /// clamped channel stops moving while the others keep going: the hue
    /// drifts (stepping #F4F6FB up far enough reaches #FFFFFF, and the blue
    /// cast is gone), and a colour with no headroom at all in the requested
    /// direction comes back unchanged. Callers that cannot use the input
    /// itself must pick the direction with room -- which is what
    /// <c>LaunchTexture.ResolveInkRgb</c> does with its luma split.</para>
    /// </remarks>
    public static uint StepLightness(uint rgb, double deltaLStar)
    {
        // Caps the walk below. Comfortable for a deltaLStar in single digits
        // (the worst case, pure black, needs 17 counts for 5.0), but it is a
        // ceiling on the dial as well as on the loop: past roughly 15 the
        // walk starts running out for mid greys and quietly under-delivering
        // rather than failing. Raise it alongside any larger step.
        const int maxChannelStep = 48;

        var r = (int)((rgb >> 16) & 0xFF);
        var g = (int)((rgb >> 8) & 0xFF);
        var b = (int)(rgb & 0xFF);

        var direction = deltaLStar >= 0 ? 1 : -1;
        var target = Lightness(LuminanceOf(r, g, b)) + deltaLStar;

        // Walk out a count at a time and stop on the first step that has
        // covered the distance. Lightness is monotonic in the step, so the
        // first hit is the closest one at or past the target, and the range
        // is small enough that searching it beats solving it.
        var step = maxChannelStep;
        for (var candidate = 1; candidate <= maxChannelStep; candidate++)
        {
            var offset = direction * candidate;
            var lightness = Lightness(LuminanceOf(
                Clamp(r + offset), Clamp(g + offset), Clamp(b + offset)));

            if (direction > 0 ? lightness >= target : lightness <= target)
            {
                step = candidate;
                break;
            }
        }

        step *= direction;
        return (uint)((Clamp(r + step) << 16) | (Clamp(g + step) << 8) | Clamp(b + step));

        static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
    }

    private static double LuminanceOf(int r, int g, int b)
        => RelativeLuminance((uint)((r << 16) | (g << 8) | b));

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
        // Fall back to whichever pole actually contrasts more, scored with the
        // same WCAG ratio as the threshold check rather than a separate
        // luminance heuristic. For mid-luminance backgrounds the two disagree
        // (e.g. 0x7F7F7F reads better on black than white), so picking by ratio
        // guarantees the most readable of black/white for any background.
        return ContrastRatio(background, 0xFFFFFFu) >= ContrastRatio(background, 0x000000u)
            ? 0xFFFFFFu
            : 0x000000u;
    }
}
