using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

// The panel-to-pixel formula a terminal surface is created and resized with.
// The desktop harness (seam-initial-size.ps1) runs on whatever display the
// machine has, which on the development machine is 100%, so the scale half
// of the formula is pinned here.
public class SurfacePixelSizeTests
{
    [Theory]
    [InlineData(800.0, 600.0, 1.0, 800u, 600u)]
    [InlineData(800.0, 600.0, 1.5, 1200u, 900u)]
    [InlineData(800.0, 600.0, 2.0, 1600u, 1200u)]
    // Fractional DIPs truncate, as every resize always has.
    [InlineData(853.3, 400.7, 1.5, 1279u, 601u)]
    [InlineData(853.3, 400.7, 2.0, 1706u, 801u)]
    public void FromDips_ScalesAndTruncates(
        double w, double h, double scale, uint wantW, uint wantH)
    {
        Assert.Equal((wantW, wantH), SurfacePixelSize.FromDips(w, h, scale, scale));
    }

    [Fact]
    public void FromDips_TakesEachAxisScaleSeparately()
    {
        Assert.Equal((1500u, 1200u), SurfacePixelSize.FromDips(1000, 600, 1.5, 2.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void A_scale_that_is_not_positive_counts_as_one(double scale)
    {
        Assert.Equal((800u, 600u), SurfacePixelSize.FromDips(800, 600, scale, scale));
        Assert.Equal((800u, 600u), SurfacePixelSize.Initial(800, 600, scale, scale));
    }

    [Fact]
    public void FromDips_NeverReportsAZeroEdge()
    {
        Assert.Equal((1u, 1u), SurfacePixelSize.FromDips(0.2, 0, 1.0, 1.0));
    }

    [Theory]
    [InlineData(0.0, 600.0)]
    [InlineData(800.0, 0.0)]
    [InlineData(-5.0, 600.0)]
    [InlineData(double.NaN, 600.0)]
    public void Initial_IsNull_UntilThePanelIsMeasured(double w, double h)
    {
        Assert.Null(SurfacePixelSize.Initial(w, h, 1.5, 1.5));
    }

    // The claim the creation path rests on: below the texture cap the size a
    // surface is created at is exactly the size the first resize pushes, so
    // that resize is a no-op and the pty is not resized at startup.
    [Theory]
    [InlineData(2560.0, 976.0, 1.0)]
    [InlineData(1706.6, 650.6, 1.5)]
    [InlineData(1280.0, 488.3, 2.0)]
    [InlineData(1279.7, 721.1, 1.25)]
    public void Initial_Equals_FromDips_BelowTheCap(double w, double h, double scale)
    {
        Assert.Equal(SurfacePixelSize.FromDips(w, h, scale, scale), SurfacePixelSize.Initial(w, h, scale, scale));
    }

    [Theory]
    [InlineData(20000.0, 900.0, 1.0, 16384u, 900u)]
    [InlineData(9000.0, 9000.0, 2.0, 16384u, 16384u)]
    [InlineData(1e12, 1e12, 1.0, 16384u, 16384u)]
    public void Initial_CapsEachEdgeAtTheTextureLimit(
        double w, double h, double scale, uint wantW, uint wantH)
    {
        Assert.Equal((wantW, wantH), SurfacePixelSize.Initial(w, h, scale, scale));
    }
}
