using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="LaunchIconMetrics"/>. The curve matters at
/// its ends: a large window must not get an absurd icon, a small one
/// must still get a legible icon, and a window whose size is not known
/// yet must get something rather than zero.
/// </summary>
public sealed class LaunchIconMetricsTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3440, 1440)]
    [InlineData(1000, 800)]
    public void Large_windows_clamp_to_the_maximum(double w, double h)
    {
        Assert.Equal(LaunchIconMetrics.MaxSizeDips, LaunchIconMetrics.Resolve(w, h));
    }

    [Theory]
    [InlineData(300, 180)]
    [InlineData(120, 120)]
    public void Small_windows_clamp_to_the_minimum(double w, double h)
    {
        Assert.Equal(LaunchIconMetrics.MinSizeDips, LaunchIconMetrics.Resolve(w, h));
    }

    [Fact]
    public void Mid_sized_windows_scale_with_the_smaller_edge()
    {
        // 500 * 0.25 = 125, between the two clamps.
        Assert.Equal(125, LaunchIconMetrics.Resolve(900, 500));
    }

    [Fact]
    public void Width_is_used_when_it_is_the_smaller_edge()
    {
        // A tall narrow window is driven by its width, not its height.
        Assert.Equal(LaunchIconMetrics.Resolve(500, 900), LaunchIconMetrics.Resolve(900, 500));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 500)]
    [InlineData(double.NaN, 500)]
    public void Unknown_or_nonsense_sizes_fall_back_to_the_maximum(double w, double h)
    {
        Assert.Equal(LaunchIconMetrics.MaxSizeDips, LaunchIconMetrics.Resolve(w, h));
    }

    [Fact]
    public void Result_is_always_within_the_clamps()
    {
        for (var edge = 1; edge <= 4000; edge += 7)
        {
            var size = LaunchIconMetrics.Resolve(edge, edge);
            Assert.InRange(size, LaunchIconMetrics.MinSizeDips, LaunchIconMetrics.MaxSizeDips);
        }
    }
}
