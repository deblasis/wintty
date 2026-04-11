using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class WindowTransparencyStateTests
{
    [Fact]
    public void FullyOpaque_DisablesTransparency()
    {
        var state = WindowTransparencyState.FromOpacity(1.0);

        Assert.False(state.IsTransparent);
        Assert.True(state.UseSystemBackdrop);
        Assert.False(state.ExtendDwmGlass);
        Assert.False(state.UseHollowClassBrush);
        Assert.Equal(1.0, state.Opacity);
    }

    [Fact]
    public void HalfOpacity_EnablesTransparency()
    {
        var state = WindowTransparencyState.FromOpacity(0.5);

        Assert.True(state.IsTransparent);
        Assert.False(state.UseSystemBackdrop);
        Assert.True(state.ExtendDwmGlass);
        Assert.True(state.UseHollowClassBrush);
        Assert.Equal(0.5, state.Opacity);
    }

    [Fact]
    public void FullyTransparent_EnablesTransparency()
    {
        var state = WindowTransparencyState.FromOpacity(0.0);

        Assert.True(state.IsTransparent);
        Assert.False(state.UseSystemBackdrop);
        Assert.True(state.ExtendDwmGlass);
        Assert.True(state.UseHollowClassBrush);
        Assert.Equal(0.0, state.Opacity);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void AnyOpacityBelowOne_IsTransparent(double opacity)
    {
        var state = WindowTransparencyState.FromOpacity(opacity);
        Assert.True(state.IsTransparent);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(100.0, 1.0)]
    public void OutOfRange_ClampedTo01(double input, double expected)
    {
        var state = WindowTransparencyState.FromOpacity(input);
        Assert.Equal(expected, state.Opacity);
    }

    [Fact]
    public void NegativeValue_ClampsToZero_IsTransparent()
    {
        var state = WindowTransparencyState.FromOpacity(-0.5);

        Assert.True(state.IsTransparent);
        Assert.Equal(0.0, state.Opacity);
    }

    [Fact]
    public void AboveOne_ClampsToOne_IsOpaque()
    {
        var state = WindowTransparencyState.FromOpacity(1.5);

        Assert.False(state.IsTransparent);
        Assert.Equal(1.0, state.Opacity);
    }

    [Fact]
    public void SameOpacity_AreEqual()
    {
        var a = WindowTransparencyState.FromOpacity(0.7);
        var b = WindowTransparencyState.FromOpacity(0.7);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void DifferentOpacity_AreNotEqual()
    {
        var a = WindowTransparencyState.FromOpacity(0.5);
        var b = WindowTransparencyState.FromOpacity(0.7);

        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void SystemBackdrop_InverseOfTransparency()
    {
        // When transparent, no system backdrop (it would be opaque behind the swap chain).
        // When opaque, system backdrop is fine.
        var transparent = WindowTransparencyState.FromOpacity(0.5);
        var opaque = WindowTransparencyState.FromOpacity(1.0);

        Assert.Equal(transparent.IsTransparent, !transparent.UseSystemBackdrop);
        Assert.Equal(opaque.IsTransparent, !opaque.UseSystemBackdrop);
    }

    [Fact]
    public void DwmGlass_MatchesTransparency()
    {
        var transparent = WindowTransparencyState.FromOpacity(0.5);
        var opaque = WindowTransparencyState.FromOpacity(1.0);

        Assert.Equal(transparent.IsTransparent, transparent.ExtendDwmGlass);
        Assert.Equal(opaque.IsTransparent, opaque.ExtendDwmGlass);
    }

    [Fact]
    public void NaN_TreatedAsOpaque()
    {
        // Math.Clamp with NaN returns NaN; NaN < 1.0 is false,
        // so NaN defaults to opaque behavior (safe fallback).
        var state = WindowTransparencyState.FromOpacity(double.NaN);

        Assert.False(state.IsTransparent);
        Assert.True(state.UseSystemBackdrop);
        Assert.False(state.ExtendDwmGlass);
    }
}
