using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public class PaneChromeTests
{
    [Fact]
    public void Gutter_covers_the_active_border_stroke()
    {
        Assert.True(PaneChrome.SurfaceInset >= PaneChrome.ActiveBorderThickness);
    }

    [Fact]
    public void Gutter_covers_the_divider_line()
    {
        // The divider rides the boundary between two leaves, so it draws
        // over one gutter or the other -- never both at once.
        Assert.True(PaneChrome.SurfaceInset >= PaneChrome.DividerThickness);
    }

    [Fact]
    public void Gutter_fill_is_opaque_background_at_full_opacity()
    {
        Assert.Equal(0xFF1E2430u, PaneChrome.GutterArgb(0x1E2430u, 1.0));
    }

    [Fact]
    public void Gutter_fill_carries_the_background_opacity()
    {
        // Matches what libghostty composites into its own window padding,
        // so the gutter cannot read as a differently tinted frame.
        Assert.Equal(0x800C0C0Cu, PaneChrome.GutterArgb(0x0C0C0Cu, 0.5));
    }

    [Theory]
    [InlineData(-1.0, 0x00u)]
    [InlineData(2.5, 0xFFu)]
    public void Gutter_fill_clamps_out_of_range_opacity(double opacity, uint alpha)
    {
        Assert.Equal(alpha, PaneChrome.GutterArgb(0x0C0C0Cu, opacity) >> 24);
    }

    [Fact]
    public void A_single_pane_fills_its_tab()
    {
        // The one case that has to collapse: the tab frame and the focus
        // frame would otherwise stroke this rectangle twice over.
        Assert.True(PaneChrome.LeafFillsContent(0, 0, 800, 600, 800, 600));
    }

    [Theory]
    // Left half of a vertical split, right half, top half, bottom half.
    [InlineData(0, 0, 400, 600)]
    [InlineData(400, 0, 400, 600)]
    [InlineData(0, 0, 800, 300)]
    [InlineData(0, 300, 800, 300)]
    public void A_split_leaf_does_not(double x, double y, double w, double h)
    {
        Assert.False(PaneChrome.LeafFillsContent(x, y, w, h, 800, 600));
    }

    [Fact]
    public void Rounding_remainders_still_count_as_filling()
    {
        // Star-sized cells arrange onto fractional DIPs. A leaf that is
        // the whole tab can report a fraction short of it, and a strict
        // comparison would leave the second stroke up on every window
        // whose width happens to land badly.
        Assert.True(PaneChrome.LeafFillsContent(0.2, 0.1, 799.6, 599.7, 800, 600));
    }

    [Fact]
    public void A_leaf_a_visible_stroke_short_does_not()
    {
        // Wider than the tolerance in one dimension only: the frames are
        // distinguishable there, so both are wanted.
        Assert.False(PaneChrome.LeafFillsContent(0, 2, 800, 598, 800, 600));
    }

    [Fact]
    public void An_unarranged_tab_is_not_filled_by_anything()
    {
        // Otherwise the first layout pass, where the tab frame has no size
        // yet but the leaf does, drops the focus frame for a frame that is
        // not drawing anything.
        Assert.False(PaneChrome.LeafFillsContent(0, 0, 800, 600, 0, 0));
    }

    [Fact]
    public void Gutter_fill_ignores_stray_high_bits_in_the_background()
    {
        // ConfigService hands back a packed RGB value; anything above the
        // low 24 bits is not colour data and must not leak into alpha.
        Assert.Equal(0xFF0C0C0Cu, PaneChrome.GutterArgb(0xAB0C0C0Cu, 1.0));
    }
}
