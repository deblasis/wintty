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
    public void Gutter_fill_ignores_stray_high_bits_in_the_background()
    {
        // ConfigService hands back a packed RGB value; anything above the
        // low 24 bits is not colour data and must not leak into alpha.
        Assert.Equal(0xFF0C0C0Cu, PaneChrome.GutterArgb(0xAB0C0C0Cu, 1.0));
    }
}
