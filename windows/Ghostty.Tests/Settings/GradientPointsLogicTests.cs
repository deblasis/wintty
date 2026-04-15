using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

public class GradientPointsLogicTests
{
    [Theory]
    [InlineData(0.5f, 0.5f, 0.5f, 0.5f)]
    [InlineData(-0.1f, 0.4f, 0.0f, 0.4f)]
    [InlineData(0.4f, 1.2f, 0.4f, 1.0f)]
    [InlineData(2.0f, -3.0f, 1.0f, 0.0f)]
    public void Clamp_ClampsXAndY(float x, float y, float expectedX, float expectedY)
    {
        var (cx, cy) = GradientPointsLogic.Clamp(x, y);
        Assert.Equal(expectedX, cx);
        Assert.Equal(expectedY, cy);
    }

    [Fact]
    public void HitTest_ReturnsNullWhenEmpty()
    {
        var result = GradientPointsLogic.HitTest(
            System.Array.Empty<(float, float)>(), 0.5f, 0.5f, 0.05f);
        Assert.Null(result);
    }

    [Fact]
    public void HitTest_ReturnsNullWhenNoneInRange()
    {
        var points = new (float, float)[] { (0.1f, 0.1f), (0.9f, 0.9f) };
        var result = GradientPointsLogic.HitTest(points, 0.5f, 0.5f, 0.05f);
        Assert.Null(result);
    }

    [Fact]
    public void HitTest_ReturnsIndexOfPointInRange()
    {
        var points = new (float, float)[] { (0.5f, 0.5f) };
        var result = GradientPointsLogic.HitTest(points, 0.52f, 0.48f, 0.05f);
        Assert.Equal(0, result);
    }

    [Fact]
    public void HitTest_PrefersLaterPointOnOverlap()
    {
        // Later points render on top, so hit-test walks from the end.
        var points = new (float, float)[] { (0.5f, 0.5f), (0.5f, 0.5f) };
        var result = GradientPointsLogic.HitTest(points, 0.5f, 0.5f, 0.05f);
        Assert.Equal(1, result);
    }

    [Fact]
    public void HitTest_BoundaryAtRadius_IsIncluded()
    {
        // Uses exactly-IEEE-754-representable values so distance equals
        // radius to the bit: point (0,0), pointer (0.5, 0), radius 0.5.
        // dx^2 + dy^2 = 0.25 exactly; r^2 = 0.25 exactly; <= holds.
        // This proves the implementation is inclusive (<=) not exclusive (<).
        var points = new (float, float)[] { (0f, 0f) };
        var result = GradientPointsLogic.HitTest(points, 0.5f, 0f, 0.5f);
        Assert.Equal(0, result);
    }
}
