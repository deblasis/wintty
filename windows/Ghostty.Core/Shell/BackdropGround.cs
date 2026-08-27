using System;

namespace Ghostty.Core.Shell;

/// <summary>
/// Estimate of the colour the window backdrop settles on, so chrome that
/// sits on the bare backdrop can choose an ink by measurement instead of
/// betting on the palette or the desktop alone.
///
/// Covers the acrylic and solid backdrops only. Acrylic is a composite: the
/// terminal palette applied as a tint at the configured tint opacity over
/// the blurred wallpaper, with the luminosity blend pulling the result back
/// towards the system's own base colour for the active OS theme. Neither
/// input predicts it on its own, and that matters most in the case a
/// single-input heuristic gets wrong: a dark palette on a light desktop
/// lands in the middle, where white and black are both weak.
///
/// The wallpaper is not an input, because an app cannot sample what the
/// compositor blurred. What makes the estimate usable for acrylic anyway is
/// how hard the luminosity blend clamps it. Measured across a full title row
/// on a wallpaper carrying both light and dark regions, the whole band
/// spanned 26 counts: #DFDFE0 to #F9F9FA with a light palette, #9F9FA3 to
/// #B9B9BC with a dark one. That is small next to the gap between a contrast
/// ratio that passes and one that does not.
///
/// Crystal is NOT estimated, and this class cannot honestly claim to cover
/// it. That backdrop is DWM blur-behind with no tint, no luminosity blend
/// and no Fluent base underneath, so the chrome sits on the wallpaper and
/// nothing clamps it. There is no colour to compute. The fallback is the
/// system base for the active OS theme, which is not a prediction of what is
/// behind the window but the surface the rest of the shell is already
/// calibrated against, so the chrome at least agrees with the element theme
/// rather than contradicting it. A crystal window over a wallpaper that
/// fights the desktop's own light/dark setting can still defeat it.
/// </summary>
public static class BackdropGround
{
    /// <summary>Fluent's SolidBackgroundFillColorBase for the light theme.</summary>
    public const uint SystemBaseLight = 0xF3F3F3u;

    /// <summary>Fluent's SolidBackgroundFillColorBase for the dark theme.</summary>
    public const uint SystemBaseDark = 0x202020u;

    /// <summary>
    /// Estimated backdrop colour under the window chrome, packed 0x00RRGGBB.
    /// </summary>
    /// <param name="paletteRgb">Terminal background, packed 0x00RRGGBB.</param>
    /// <param name="osDark">True when the desktop is in dark mode.</param>
    /// <param name="backdropStyle">
    /// Current backdrop style, lowercased (see <see cref="BackdropStyles"/>).
    /// </param>
    /// <param name="tintOpacity">
    /// Resolved <c>background-tint-opacity</c>. Not the default constant: at
    /// 0.9 the palette all but replaces the base and an estimate pinned to
    /// 0.3 is off by threefold.
    /// </param>
    public static uint Estimate(
        uint paletteRgb,
        bool osDark,
        string backdropStyle,
        double tintOpacity = AcrylicTintResolver.DefaultTintOpacity)
    {
        var baseRgb = osDark ? SystemBaseDark : SystemBaseLight;

        // Nothing tints the chrome under crystal, so there is nothing to
        // blend and the base stands alone. See the class remarks.
        if (backdropStyle == BackdropStyles.Crystal) return baseRgb;

        // A solid backdrop is not a composite. There the root grid is painted
        // with the opaque chrome colour and nothing tints it, so that colour
        // is the ground outright. Asked through the same resolver that paints
        // the root rather than by re-deciding here, because the two silently
        // disagreeing is how chrome ends up calibrated against a surface it is
        // not actually drawn on: black ink on #0C0C0C, measured at 1.1:1.
        var root = RootBackgroundResolver.Resolve(
            backdropStyle, shellThemeEnabled: false, shellThemeBgArgb: 0, osDark);
        if (root != RootBackgroundResolver.TransparentArgb)
            return root & 0x00FFFFFFu;

        var tint = Math.Clamp(tintOpacity, 0.0, 1.0);
        var r = Mix((paletteRgb >> 16) & 0xFF, (baseRgb >> 16) & 0xFF, tint);
        var g = Mix((paletteRgb >> 8) & 0xFF, (baseRgb >> 8) & 0xFF, tint);
        var b = Mix(paletteRgb & 0xFF, baseRgb & 0xFF, tint);
        return (r << 16) | (g << 8) | b;

        static uint Mix(uint over, uint under, double alpha)
            => (uint)((alpha * over) + ((1.0 - alpha) * under) + 0.5);
    }
}
