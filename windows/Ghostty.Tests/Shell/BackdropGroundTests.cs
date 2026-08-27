using Ghostty.Core.Shell;
using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// The vertical title row is the bare window backdrop, so the ink drawn on
/// it is chosen by contrast against <see cref="BackdropGround"/>'s estimate
/// of that backdrop. These cover the estimate and, more to the point, the
/// pole it makes the caller pick: the two configurations below are the ones
/// a single-input heuristic gets wrong, and both were measured wrong on a
/// live window before the estimate existed.
/// </summary>
public sealed class BackdropGroundTests
{
    private const uint LightPalette = 0xF4F6FBu;
    private const uint DarkPalette = 0x1E1E2Eu;

    private static uint Ink(uint palette, bool osDark, bool elementDark) =>
        ThemeResolution.EnsureReadableForeground(
            BackdropGround.Estimate(palette, osDark, BackdropStyles.Frosted),
            elementDark ? 0xFFFFFFu : 0x000000u);

    [Fact]
    public void Estimate_sits_between_the_palette_and_the_system_base()
    {
        var ground = BackdropGround.Estimate(DarkPalette, osDark: false, BackdropStyles.Frosted);
        Assert.True(ground > DarkPalette);
        Assert.True(ground < BackdropGround.SystemBaseLight);
    }

    [Theory]
    [InlineData(true, BackdropGround.SystemBaseDark)]
    [InlineData(false, BackdropGround.SystemBaseLight)]
    public void A_palette_equal_to_the_base_leaves_the_base_alone(bool osDark, uint expected)
    {
        Assert.Equal(expected, BackdropGround.Estimate(expected, osDark, BackdropStyles.Frosted));
    }

    /// <summary>
    /// A dark theme on a light desktop. The backdrop lands mid grey, where
    /// the palette's own answer (white, because the palette is dark) reads
    /// at 2.57:1 -- measured on a live window. Black is the pole that holds.
    /// </summary>
    [Fact]
    public void Dark_palette_on_a_light_desktop_takes_black_not_the_palette_pole()
    {
        var ground = BackdropGround.Estimate(DarkPalette, osDark: false, BackdropStyles.Frosted);
        Assert.Equal(0x000000u, Ink(DarkPalette, osDark: false, elementDark: true));
        Assert.True(ThemeResolution.ContrastRatio(ground, 0x000000u) >= 4.5);
    }

    /// <summary>
    /// window-theme=dark over a light palette on a light desktop. The
    /// element theme's own answer is white, and the row it lands on stays
    /// light because the backdrop tints from the palette, not the theme.
    /// </summary>
    [Fact]
    public void A_dark_element_theme_over_a_light_row_is_overridden()
    {
        var ground = BackdropGround.Estimate(LightPalette, osDark: false, BackdropStyles.Frosted);
        Assert.Equal(0x000000u, Ink(LightPalette, osDark: false, elementDark: true));
        Assert.True(ThemeResolution.ContrastRatio(ground, 0xFFFFFFu) < 4.5);
    }

    /// <summary>
    /// A solid backdrop has no composite to estimate: the root grid is the
    /// opaque chrome colour and the chrome is drawn straight onto it. Read as
    /// a composite it came out near-white, which put black ink on #0C0C0C at
    /// 1.1:1 on a live window.
    /// </summary>
    [Theory]
    [InlineData(BackdropStyles.Solid)]
    [InlineData("something-nobody-has-added-yet")]
    public void A_solid_backdrop_is_the_root_fill_not_a_blend(string style)
    {
        var ground = BackdropGround.Estimate(LightPalette, osDark: false, style);
        Assert.Equal(RootBackgroundResolver.OpaqueChromeArgb & 0x00FFFFFFu, ground);
        Assert.Equal(
            0xFFFFFFu,
            ThemeResolution.EnsureReadableForeground(ground, 0x000000u));
    }

    [Fact]
    public void Agreeing_inputs_keep_the_element_theme_pole()
    {
        Assert.Equal(0x000000u, Ink(LightPalette, osDark: false, elementDark: false));
        Assert.Equal(0xFFFFFFu, Ink(DarkPalette, osDark: true, elementDark: true));
    }

    /// <summary>
    /// Whatever the pair of inputs, the ink that comes back clears AA
    /// against the ground it was chosen for. That is the whole contract.
    /// </summary>
    [Theory]
    [InlineData(LightPalette, true)]
    [InlineData(LightPalette, false)]
    [InlineData(DarkPalette, true)]
    [InlineData(DarkPalette, false)]
    [InlineData(0x808080u, true)]
    [InlineData(0x808080u, false)]
    public void Every_combination_clears_AA(uint palette, bool osDark)
    {
        foreach (var elementDark in new[] { true, false })
        {
            var ground = BackdropGround.Estimate(palette, osDark, BackdropStyles.Frosted);
            var ink = Ink(palette, osDark, elementDark);
            Assert.True(
                ThemeResolution.ContrastRatio(ground, ink) >= 4.5,
                $"palette {palette:X6}, osDark {osDark}, elementDark {elementDark}: "
                + $"ground {ground:X6} against ink {ink:X6} is "
                + $"{ThemeResolution.ContrastRatio(ground, ink):F2}:1");
        }
    }

    /// <summary>
    /// Crystal is DWM blur-behind: no tint, no luminosity blend, no Fluent
    /// base. There is nothing to composite, so the palette must not leak into
    /// the answer -- modelled as acrylic it returned a near-white ground for a
    /// light palette and put black ink over whatever wallpaper was there.
    /// </summary>
    [Theory]
    [InlineData(true, BackdropGround.SystemBaseDark)]
    [InlineData(false, BackdropGround.SystemBaseLight)]
    public void Crystal_is_not_modelled_as_a_blend(bool osDark, uint expected)
    {
        Assert.Equal(
            expected,
            BackdropGround.Estimate(LightPalette, osDark, BackdropStyles.Crystal));
        Assert.Equal(
            expected,
            BackdropGround.Estimate(DarkPalette, osDark, BackdropStyles.Crystal));
    }

    /// <summary>
    /// The tint opacity is the user's, not the default constant. At 0.9 the
    /// palette all but replaces the base, and an estimate pinned to 0.3 is
    /// off by threefold in the direction that matters.
    /// </summary>
    [Fact]
    public void A_configured_tint_opacity_moves_the_estimate()
    {
        var light = BackdropGround.Estimate(
            DarkPalette, osDark: false, BackdropStyles.Frosted, tintOpacity: 0.3);
        var heavy = BackdropGround.Estimate(
            DarkPalette, osDark: false, BackdropStyles.Frosted, tintOpacity: 0.9);

        Assert.True(heavy < light, "a heavier tint of a dark palette must darken the ground");
        Assert.True(
            ThemeResolution.ContrastRatio(heavy, DarkPalette)
                < ThemeResolution.ContrastRatio(light, DarkPalette),
            "at 0.9 the ground should sit much closer to the palette");
    }

    /// <summary>
    /// The colour the estimate blends is the tint the compositor lays down,
    /// which is the user's background-tint-color when one is set, not the
    /// palette. Mirrors how the shell feeds it: the resolver resolves, the
    /// estimate consumes the resolved RGB and opacity together. A diverging
    /// tint on a light desktop moves the ground a long way from what the
    /// palette alone would have predicted, and the ink still has to clear
    /// AA against it.
    /// </summary>
    [Fact]
    public void A_diverging_tint_color_is_the_ground_not_the_palette()
    {
        const uint tintOverride = 0xF2E8DCu;
        var tuning = AcrylicTintResolver.Resolve(
            tintOverrideArgb: tintOverride,
            themeBackgroundRgb: DarkPalette,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: false,
            backgroundOpacity: 1.0);

        var ground = BackdropGround.Estimate(
            tuning.TintArgb & 0x00FFFFFFu,
            osDark: false,
            BackdropStyles.Frosted,
            tuning.TintOpacity);

        Assert.Equal(
            BackdropGround.Estimate(tintOverride, osDark: false, BackdropStyles.Frosted),
            ground);
        Assert.NotEqual(
            BackdropGround.Estimate(DarkPalette, osDark: false, BackdropStyles.Frosted),
            ground);

        foreach (var elementDark in new[] { true, false })
        {
            var ink = ThemeResolution.EnsureReadableForeground(
                ground, elementDark ? 0xFFFFFFu : 0x000000u);
            Assert.True(
                ThemeResolution.ContrastRatio(ground, ink) >= 4.5,
                $"elementDark {elementDark}: ground {ground:X6} against ink {ink:X6} is "
                + $"{ThemeResolution.ContrastRatio(ground, ink):F2}:1");
        }
    }
}
