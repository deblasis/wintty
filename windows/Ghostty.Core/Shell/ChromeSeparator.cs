using Ghostty.Core.Windows;

namespace Ghostty.Core.Shell;

/// <summary>
/// The colour of a boundary stroke drawn between two chrome surfaces.
///
/// Chrome that is bare backdrop cannot separate itself from the terminal by
/// being a different shade, because the backdrop is not a shade anyone picks:
/// on a light desktop with a light palette the strip, the rows and the
/// terminal all land within a few counts of each other, and the whole
/// boundary reads at about 1.1:1. A surface cannot fix that without giving up
/// the backdrop. A stroke can, because a stroke is one line whose colour is
/// ours to choose.
///
/// The walk is in CIE L* rather than in channel counts, so "far enough to
/// see" means the same thing on a light palette as on a dark one. sRGB is
/// gamma encoded, and a fixed number of counts buys markedly more visible
/// separation at the black end than at the white end; a stroke tuned on a
/// dark terminal and reused on a light one comes out nearly invisible.
/// </summary>
public static class ChromeSeparator
{
    /// <summary>
    /// WCAG's threshold for a non-text visual boundary. The same number the
    /// selected row's accent already clears against its neighbours.
    /// </summary>
    public const double DefaultMinContrast = 3.0;

    /// <summary>
    /// How far each pass walks, in L*. <see cref="ThemeResolution.StepLightness"/>
    /// caps a single call at 48 channel counts and under-delivers past roughly
    /// 15, so reaching a ratio takes several bounded passes rather than one
    /// large one. Kept well inside the range that helper is accurate over,
    /// and small because the loop tests before it steps: the last pass can
    /// only overshoot the threshold by this much, and at 12 that was enough
    /// to make a line meant as a hairline read heavier than intended.
    /// </summary>
    private const double StepLStar = 4.0;

    // A termination bound, not a tuning knob: 26 passes of 4 L* covers the
    // whole 0..100 range. The pole below catches any ground the walk rails
    // out on before it gets there.
    private const int MaxPasses = 26;

    /// <summary>
    /// <paramref name="desiredRgb"/> moved just far enough in lightness to
    /// clear <paramref name="minContrast"/> against
    /// <paramref name="groundRgb"/>, keeping its hue.
    ///
    /// For a colour that carries meaning rather than just marking a boundary.
    /// The pane frame and the selected tab's folder stroke are drawn in the
    /// user's accent, and replacing that outright with a neutral costs the
    /// identity it was carrying: a theme whose accent is 2.82:1 against its
    /// own background is marginal, not meaningless, and it should come back
    /// as a lighter version of itself rather than as grey.
    ///
    /// Falls through to <see cref="Resolve"/> only when the hue has no room
    /// left, which is where identity has already been lost anyway.
    /// </summary>
    public static uint EnsureVisible(
        uint groundRgb, uint desiredRgb, double minContrast = DefaultMinContrast)
    {
        if (ThemeResolution.ContrastRatio(groundRgb, desiredRgb) >= minContrast)
            return desiredRgb;

        var direction = ThemeResolution.IsBackgroundDark(groundRgb) ? 1.0 : -1.0;
        var candidate = desiredRgb;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = ThemeResolution.StepLightness(candidate, direction * StepLStar);
            if (next == candidate) break;
            candidate = next;
            if (ThemeResolution.ContrastRatio(groundRgb, candidate) >= minContrast)
                return candidate;
        }

        return Resolve(groundRgb, minContrast);
    }

    /// <summary>
    /// A tint of <paramref name="groundRgb"/> that clears
    /// <paramref name="minContrast"/> against it, packed 0x00RRGGBB.
    ///
    /// Walks away from the ground in whichever direction has headroom, so the
    /// stroke stays a tint of the surface it divides rather than becoming a
    /// colour of its own. If the ground has no headroom left in that
    /// direction the walk stalls and the pole is used instead.
    /// </summary>
    public static uint Resolve(uint groundRgb, double minContrast = DefaultMinContrast)
    {
        var direction = ThemeResolution.IsBackgroundDark(groundRgb) ? 1.0 : -1.0;

        var candidate = groundRgb;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            if (ThemeResolution.ContrastRatio(groundRgb, candidate) >= minContrast)
                return candidate;

            var next = ThemeResolution.StepLightness(candidate, direction * StepLStar);
            // A channel that has hit its rail stops moving, and every further
            // pass returns the same colour. Take the pole rather than spin.
            if (next == candidate) break;
            candidate = next;
        }

        if (ThemeResolution.ContrastRatio(groundRgb, candidate) >= minContrast)
            return candidate;

        // Scored, not inferred from the direction the walk took. That
        // direction comes from IsBackgroundDark, a BT.709 test on
        // gamma-encoded channels, and the two disagree for mid-luminance
        // grounds -- the same disagreement EnsureReadableForeground documents
        // and picks by ratio to avoid. Inferring it here put white on
        // #FF4BFF at 2.73:1 where black gives 7.70:1. Scoring both poles is
        // at worst 4.58:1 for any ground, since no colour can be close to
        // both ends at once.
        return ThemeResolution.ContrastRatio(groundRgb, 0xFFFFFFu)
            >= ThemeResolution.ContrastRatio(groundRgb, 0x000000u)
                ? 0xFFFFFFu
                : 0x000000u;
    }
}
