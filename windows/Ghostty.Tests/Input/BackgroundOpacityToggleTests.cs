using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class BackgroundOpacityToggleTests
{
    [Fact]
    public void Transparent_GoesOpaque_AndRemembersBaseline()
    {
        var r = BackgroundOpacityToggle.Next(current: 0.8, baseline: null);
        Assert.Equal(1.0, r.OpacityToWrite);
        Assert.Equal(0.8, r.NewBaseline);
    }

    [Fact]
    public void Opaque_WithBaseline_RestoresAndClears()
    {
        var r = BackgroundOpacityToggle.Next(current: 1.0, baseline: 0.8);
        Assert.Equal(0.8, r.OpacityToWrite);
        Assert.Null(r.NewBaseline);
    }

    [Fact]
    public void Opaque_NoBaseline_IsNoOp()
    {
        var r = BackgroundOpacityToggle.Next(current: 1.0, baseline: null);
        Assert.Null(r.OpacityToWrite);
        Assert.Null(r.NewBaseline);
    }

    [Fact]
    public void Opaque_WithFullyOpaqueBaseline_IsNoOp()
    {
        // A baseline that is itself fully opaque reveals nothing.
        var r = BackgroundOpacityToggle.Next(current: 1.0, baseline: 1.0);
        Assert.Null(r.OpacityToWrite);
    }
}
