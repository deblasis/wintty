using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Unselected tab titles are drawn at 70% alpha over the tab strip. Under
/// window-theme=wintty with a frosted or crystal frame the strip is not
/// painted at all, so the shade the palette names for it is a colour nothing
/// renders and the ink lands on the window's backdrop instead.
///
/// Captured live: palette wintty-light, dark desktop, frosted frame. The
/// strip rendered #555657 while the palette named #E9EBF1, and a pole picked
/// against the palette put black ink at 2.37:1 where white would have been at
/// 4.62:1.
///
/// Arithmetic, not wiring. What the strip does with the answer is only
/// observable on a live window; which answer is correct is a number.
/// </summary>
public sealed class InactiveTabInkContrastTests
{
    /// <summary>The strip's measured shade in the capture.</summary>
    private const uint FrostedStripGround = 0x555657u;

    /// <summary>wintty-light's tab-bar background: the shade nothing painted.</summary>
    private const uint PaletteTabBarBackground = 0xE9EBF1u;

    /// <summary>The de-emphasis the unselected titles are drawn at.</summary>
    private const byte InactiveInkAlpha = 0xB3;

    private const double WcagAA = 4.5;

    private const uint White = 0xFFFFFFu;
    private const uint Black = 0x000000u;

    private static uint Composited(uint pole, uint ground)
        => ThemeResolution.CompositeOver(pole, InactiveInkAlpha, ground);

    /// <summary>
    /// What the reader actually sees: the composited ink against the ground
    /// it was composited onto, never the pole against the ground.
    /// </summary>
    private static double RatioOnGround(uint pole, uint ground)
        => ThemeResolution.ContrastRatio(Composited(pole, ground), ground);

    [Fact]
    public void MutedInk_CompositesToTheCapturedColours()
    {
        Assert.Equal(0x191A1Au, Composited(Black, FrostedStripGround));
        Assert.Equal(0xCCCDCDu, Composited(White, FrostedStripGround));
    }

    [Fact]
    public void OnTheFrostedStrip_BlackFailsAA_AndWhiteClearsIt()
    {
        Assert.Equal(2.37, RatioOnGround(Black, FrostedStripGround), 2);
        Assert.Equal(4.62, RatioOnGround(White, FrostedStripGround), 2);

        Assert.True(RatioOnGround(Black, FrostedStripGround) < WcagAA);
        Assert.True(RatioOnGround(White, FrostedStripGround) >= WcagAA);
    }

    /// <summary>
    /// The regression itself. The pole was asked for against
    /// TabBarBackground, which is light, so it came back black -- and black is
    /// the pole that fails on the shade the strip really rendered.
    /// </summary>
    [Fact]
    public void TheGroundIsTheStrip_NotTheShadeThePaletteNamed()
    {
        Assert.False(ThemeResolution.PreferLightForegroundAtAlpha(
            PaletteTabBarBackground, InactiveInkAlpha));

        Assert.True(ThemeResolution.PreferLightForegroundAtAlpha(
            FrostedStripGround, InactiveInkAlpha));
    }

    [Fact]
    public void ThePoleChosenAgainstTheStrip_ClearsAA()
    {
        var light = ThemeResolution.PreferLightForegroundAtAlpha(
            FrostedStripGround, InactiveInkAlpha);

        Assert.True(RatioOnGround(light ? White : Black, FrostedStripGround) >= WcagAA);
    }

    /// <summary>
    /// A solid frame paints the strip with the palette's own shade, so the
    /// ground and the shade are one colour and the answer is the one the base
    /// branch already gave: black, at 7.93:1.
    /// </summary>
    [Fact]
    public void UnderASolidFrame_ThePaletteShadeIsTheGround_AndNothingMoves()
    {
        Assert.False(ThemeResolution.PreferLightForegroundAtAlpha(
            PaletteTabBarBackground, InactiveInkAlpha));
        Assert.Equal(7.93, RatioOnGround(Black, PaletteTabBarBackground), 2);
        Assert.True(RatioOnGround(Black, PaletteTabBarBackground) >= WcagAA);
    }

    /// <summary>
    /// The alpha is part of the question rather than a detail applied after
    /// it. On a mid grey the opaque poles and the muted ones disagree: white
    /// is the better opaque ink and the worse muted one, because muting pulls
    /// both candidates back towards the ground by different amounts.
    ///
    /// Mid greys are not a corner case here. They are what a translucent
    /// frame makes of the chrome whenever the palette and the desktop
    /// disagree, which is the combination this whole calibration exists for.
    /// </summary>
    [Fact]
    public void TheAlpha_CanFlipTheAnswer()
    {
        const uint MidGrey = 0x747474u;

        Assert.True(
            ThemeResolution.ContrastRatio(MidGrey, White)
                > ThemeResolution.ContrastRatio(MidGrey, Black),
            "opaque white is the better pole on this ground");
        Assert.True(
            RatioOnGround(Black, MidGrey) > RatioOnGround(White, MidGrey),
            "muted black is the better pole on this ground");

        Assert.False(ThemeResolution.PreferLightForegroundAtAlpha(MidGrey, InactiveInkAlpha));
    }

    /// <summary>
    /// Fully opaque ink is the ink itself, so the alpha-aware pick collapses
    /// onto the plain WCAG one there. Both poles, so a composite that dropped
    /// the ground entirely would still be caught.
    /// </summary>
    [Theory]
    [InlineData(0x000000u)]
    [InlineData(0xFFFFFFu)]
    [InlineData(0x555657u)]
    [InlineData(0xE9EBF1u)]
    public void AtFullAlpha_TheInkIsUnchangedByTheGround(uint ground)
    {
        Assert.Equal(White, ThemeResolution.CompositeOver(White, 0xFF, ground));
        Assert.Equal(Black, ThemeResolution.CompositeOver(Black, 0xFF, ground));
    }

    /// <summary>
    /// Zero alpha is the ground back, unchanged. The rounding runs over three
    /// channels independently and an off-by-one there is exactly the kind of
    /// thing that never shows up in a ratio.
    /// </summary>
    [Theory]
    [InlineData(0x000000u)]
    [InlineData(0xFFFFFFu)]
    [InlineData(0x555657u)]
    [InlineData(0xE9EBF1u)]
    public void AtZeroAlpha_TheGroundIsUnchanged(uint ground)
    {
        Assert.Equal(ground, ThemeResolution.CompositeOver(White, 0x00, ground));
        Assert.Equal(ground, ThemeResolution.CompositeOver(Black, 0x00, ground));
    }
}
