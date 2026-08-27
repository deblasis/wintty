using Ghostty.Core.Shell;
using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// The vertical strip and the title row are bare backdrop, so the boundary
/// between them and the terminal is a stroke rather than a change of surface.
/// The stroke only does its job if it clears 3.0:1 against the surface it
/// divides, and the whole point of deriving it is that no fixed colour clears
/// that for every palette.
/// </summary>
public sealed class ChromeSeparatorTests
{
    // The two built-in palettes, plus the mid greys that are hardest to
    // separate from anything.
    [Theory]
    [InlineData(0xF4F6FBu)]
    [InlineData(0x1E1E2Eu)]
    [InlineData(0x000000u)]
    [InlineData(0xFFFFFFu)]
    [InlineData(0x808080u)]
    [InlineData(0x7F7F7Fu)]
    [InlineData(0x0C0C0Cu)]
    [InlineData(0xC0392Bu)]
    [InlineData(0x2E8B57u)]
    // Grounds where the walk rails out and the fallback pole decides. Both
    // came back under 3.0:1 while that pole was inferred from the walk's
    // direction rather than scored: #00AF00 is reachable from a config
    // (background = #00af00), #FF4BFF was the worst of 154 such grounds.
    [InlineData(0x00AF00u)]
    [InlineData(0xFF4BFFu)]
    public void The_stroke_clears_the_threshold_against_its_ground(uint ground)
    {
        var stroke = ChromeSeparator.Resolve(ground);
        var ratio = ThemeResolution.ContrastRatio(ground, stroke);
        Assert.True(
            ratio >= ChromeSeparator.DefaultMinContrast,
            $"ground {ground:X6} against stroke {stroke:X6} is {ratio:F2}:1");
        // Never the ground itself, which is the one answer that draws
        // nothing. Pure white and pure black are in the set above because
        // both are real terminal backgrounds and neither is stuck: the
        // direction the luminance test picks points back into the range.
        Assert.NotEqual(ground, stroke);
    }

    /// <summary>
    /// Away from the ground's own end of the range, so the walk has somewhere
    /// to go. A light terminal darkens, a dark one lightens.
    /// </summary>
    [Fact]
    public void The_walk_goes_where_there_is_headroom()
    {
        Assert.True(ChromeSeparator.Resolve(0xF4F6FBu) < 0xF4F6FBu);
        Assert.True(ChromeSeparator.Resolve(0x1E1E2Eu) > 0x1E1E2Eu);
    }

    /// <summary>
    /// The case that rules out picking one value and shipping it. A mid grey
    /// terminal is what any fixed mid grey stroke disappears into, and mid
    /// grey is exactly where a stroke chosen against the two built-in
    /// palettes lands -- both of those sit near a rail, so a single value
    /// clears them both and looks like it would do.
    /// </summary>
    [Fact]
    public void A_fixed_stroke_vanishes_on_the_ground_it_was_not_chosen_for()
    {
        var chosenForLight = ChromeSeparator.Resolve(0xF4F6FBu);
        var awkward = 0x808080u;

        Assert.True(
            ThemeResolution.ContrastRatio(awkward, chosenForLight) < 3.0,
            "a stroke picked against a light palette should be expected to "
            + "fail on a mid grey one, or this test proves nothing");
        Assert.True(
            ThemeResolution.ContrastRatio(awkward, ChromeSeparator.Resolve(awkward)) >= 3.0);
    }

    [Fact]
    public void A_higher_threshold_is_honoured()
    {
        var stroke = ChromeSeparator.Resolve(0xF4F6FBu, minContrast: 7.0);
        Assert.True(ThemeResolution.ContrastRatio(0xF4F6FBu, stroke) >= 7.0);
    }


    /// <summary>
    /// The guarantee, swept rather than argued: every ground in a stepped
    /// walk of the sRGB cube gets a stroke that clears the threshold. This is
    /// the test that would have caught the fallback pole being inferred from
    /// the walk's direction instead of scored -- 154 grounds failed it, worst
    /// 2.73:1.
    /// </summary>
    [Fact]
    public void Every_ground_in_the_cube_gets_a_stroke_that_clears()
    {
        var worst = double.MaxValue;
        uint worstGround = 0;

        for (uint r = 0; r < 256; r += 8)
        for (uint g = 0; g < 256; g += 8)
        for (uint b = 0; b < 256; b += 8)
        {
            var ground = (r << 16) | (g << 8) | b;
            var ratio = ThemeResolution.ContrastRatio(
                ground, ChromeSeparator.Resolve(ground));
            if (ratio < worst) { worst = ratio; worstGround = ground; }
        }

        Assert.True(
            worst >= ChromeSeparator.DefaultMinContrast,
            $"worst ground {worstGround:X6} came back at {worst:F2}:1");
    }

    /// <summary>
    /// An accent that already reads is handed back untouched -- the common
    /// case, and the one where moving it would be vandalism.
    /// </summary>
    [Fact]
    public void A_readable_accent_is_left_alone()
    {
        Assert.Equal(0x1668C4u, ChromeSeparator.EnsureVisible(0xF4F6FBu, 0x1668C4u));
    }

    /// <summary>
    /// A marginal accent keeps its hue. #1668C4 on #1E1E2E measures 2.82:1 --
    /// under the bar, but it is still the theme's blue, and coming back as
    /// grey loses what it was carrying.
    /// </summary>
    [Fact]
    public void A_marginal_accent_is_lightened_not_replaced()
    {
        const uint ground = 0x1E1E2Eu;
        var moved = ChromeSeparator.EnsureVisible(ground, 0x1668C4u);

        Assert.True(ThemeResolution.ContrastRatio(ground, moved) >= 3.0);
        var r = (moved >> 16) & 0xFF;
        var b = moved & 0xFF;
        Assert.True(b > r + 40, $"{moved:X6} should still read as blue");
    }

    /// <summary>
    /// White on a light terminal is the failure issue 754 reports, and white
    /// has no hue to preserve, so it comes back as a neutral that reads.
    /// </summary>
    [Fact]
    public void An_accent_with_no_hue_left_falls_through_to_a_neutral()
    {
        const uint ground = 0xF4F6FBu;
        var moved = ChromeSeparator.EnsureVisible(ground, 0xFFFFFFu);
        Assert.True(ThemeResolution.ContrastRatio(ground, moved) >= 3.0);
    }

    [Theory]
    [InlineData(0xF4F6FBu, 0xFFFFFFu)]
    [InlineData(0x1E1E2Eu, 0x1668C4u)]
    [InlineData(0x131620u, 0x14161Fu)]
    [InlineData(0x000000u, 0x000000u)]
    [InlineData(0xFFFFFFu, 0xFFFFFFu)]
    public void Every_accent_comes_back_visible(uint ground, uint accent)
    {
        var ratio = ThemeResolution.ContrastRatio(
            ground, ChromeSeparator.EnsureVisible(ground, accent));
        Assert.True(ratio >= 3.0, $"ground {ground:X6} accent {accent:X6} -> {ratio:F2}:1");
    }
}
