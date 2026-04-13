using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class SnapZoneCatalogTests
{
    [Theory]
    [InlineData(1920, 1080, (int)SnapMonitorShape.StandardLandscape)] // 16:9
    [InlineData(2560, 1600, (int)SnapMonitorShape.StandardLandscape)] // 16:10
    [InlineData(1920, 1200, (int)SnapMonitorShape.StandardLandscape)] // 16:10
    [InlineData(3440, 1440, (int)SnapMonitorShape.UltraWideLandscape)] // 21:9 = 2.388
    [InlineData(5120, 1440, (int)SnapMonitorShape.UltraWideLandscape)] // 32:9 = 3.555
    [InlineData(1080, 1920, (int)SnapMonitorShape.Portrait)]
    [InlineData(1200, 1920, (int)SnapMonitorShape.Portrait)]
    [InlineData(1000, 1000, (int)SnapMonitorShape.StandardLandscape)] // square -> landscape
    public void Classify_returns_expected_shape(int w, int h, int expected)
    {
        Assert.Equal((SnapMonitorShape)expected, SnapZoneCatalog.Classify(w, h));
    }

    [Fact]
    public void Classify_threshold_2point1_exact_is_ultrawide()
    {
        // 2100 x 1000 = 2.1 exact -> ultra-wide (>= 2.1)
        Assert.Equal(SnapMonitorShape.UltraWideLandscape, SnapZoneCatalog.Classify(2100, 1000));
    }

    [Fact]
    public void Classify_just_below_threshold_is_standard()
    {
        // 2099 x 1000 = 2.099 -> standard
        Assert.Equal(SnapMonitorShape.StandardLandscape, SnapZoneCatalog.Classify(2099, 1000));
    }

    [Fact]
    public void ZonesFor_standard_landscape_returns_halves_quarters_plus_maximize()
    {
        var zones = SnapZoneCatalog.ZonesFor(1920, 1080);
        Assert.Equal(new[]
        {
            SnapZone.LeftHalf, SnapZone.RightHalf,
            SnapZone.TopHalf, SnapZone.BottomHalf,
            SnapZone.TopLeftQuarter, SnapZone.TopRightQuarter,
            SnapZone.BottomLeftQuarter, SnapZone.BottomRightQuarter,
            SnapZone.Maximize,
        }, zones);
    }

    [Fact]
    public void ZonesFor_ultrawide_returns_halves_thirds_quarters_plus_maximize()
    {
        var zones = SnapZoneCatalog.ZonesFor(3440, 1440);
        Assert.Equal(new[]
        {
            SnapZone.LeftHalf, SnapZone.RightHalf,
            SnapZone.LeftThird, SnapZone.MiddleThird, SnapZone.RightThird,
            SnapZone.LeftTwoThirds, SnapZone.RightTwoThirds,
            SnapZone.TopLeftQuarter, SnapZone.TopRightQuarter,
            SnapZone.BottomLeftQuarter, SnapZone.BottomRightQuarter,
            SnapZone.Maximize,
        }, zones);
    }

    [Fact]
    public void ZonesFor_portrait_returns_halves_horizontal_thirds_plus_maximize()
    {
        var zones = SnapZoneCatalog.ZonesFor(1080, 1920);
        Assert.Equal(new[]
        {
            SnapZone.TopHalf, SnapZone.BottomHalf,
            SnapZone.TopThird, SnapZone.MiddleThirdHorizontal, SnapZone.BottomThird,
            SnapZone.Maximize,
        }, zones);
    }
}
