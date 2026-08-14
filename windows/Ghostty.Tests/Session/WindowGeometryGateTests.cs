using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

/// <summary>
/// Unit tests for <see cref="WindowGeometryGate"/>. These rules decide where
/// both the window and the pre-XAML splash go, and the two used to hold
/// separate copies of them; a divergence here puts the splash on a rect the
/// window does not use, which is the failure the splash exists to prevent.
/// </summary>
public sealed class WindowGeometryGateTests
{
    private static WindowGeometry Geometry(
        int? x = 0, int? y = 0, int? w = 800, int? h = 600, bool maximized = false) =>
        new() { X = x, Y = y, Width = w, Height = h, Maximized = maximized };

    [Fact]
    public void NullGeometryIsRejected()
    {
        Assert.False(WindowGeometryGate.TryNormalize(null, out _));
    }

    [Fact]
    public void PlausibleGeometryPassesThroughUnchanged()
    {
        Assert.True(WindowGeometryGate.TryNormalize(
            Geometry(x: 120, y: 80, w: 1024, h: 768), out var rect));
        Assert.Equal((120, 80, 1024, 768), rect);
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(800, null)]
    [InlineData(null, null)]
    public void MissingSizeIsRejected(int? w, int? h)
    {
        Assert.False(WindowGeometryGate.TryNormalize(Geometry(w: w, h: h), out _));
    }

    [Theory]
    [InlineData(WindowGeometryGate.MinWidth - 1, 600)]
    [InlineData(800, WindowGeometryGate.MinHeight - 1)]
    public void UndersizedGeometryIsRejected(int w, int h)
    {
        Assert.False(WindowGeometryGate.TryNormalize(Geometry(w: w, h: h), out _));
    }

    [Fact]
    public void MinimumSizeIsAccepted()
    {
        Assert.True(WindowGeometryGate.TryNormalize(
            Geometry(w: WindowGeometryGate.MinWidth, h: WindowGeometryGate.MinHeight),
            out var rect));
        Assert.Equal(
            (0, 0, WindowGeometryGate.MinWidth, WindowGeometryGate.MinHeight), rect);
    }

    /// <summary>
    /// The rect saved when a window is closed while minimized. Honouring it
    /// would put the window, and the splash covering it, off-screen.
    /// </summary>
    [Fact]
    public void MinimizedPlaceholderRectIsRejected()
    {
        Assert.False(WindowGeometryGate.TryNormalize(
            Geometry(x: -32000, y: -32000, w: 160, h: 31), out _));
    }

    /// <summary>
    /// A null position means "wherever the OS put it", not "discard this".
    /// Rejecting it would have sent the splash to a fallback while the window
    /// went to the origin.
    /// </summary>
    [Theory]
    [InlineData(null, 40, 0, 40)]
    [InlineData(40, null, 40, 0)]
    [InlineData(null, null, 0, 0)]
    public void NullPositionNormalizesToTheOrigin(int? x, int? y, int wantX, int wantY)
    {
        Assert.True(WindowGeometryGate.TryNormalize(Geometry(x: x, y: y), out var rect));
        Assert.Equal(wantX, rect.X);
        Assert.Equal(wantY, rect.Y);
    }

    /// <summary>
    /// A negative position is legitimate on a multi-monitor desktop, where
    /// the primary display is not necessarily the top-left one.
    /// </summary>
    [Fact]
    public void NegativePositionIsPreserved()
    {
        Assert.True(WindowGeometryGate.TryNormalize(
            Geometry(x: -1920, y: -200), out var rect));
        Assert.Equal(-1920, rect.X);
        Assert.Equal(-200, rect.Y);
    }

    /// <summary>
    /// The maximized flag is the caller's business: the window replays it
    /// with SW_SHOWMAXIMIZED and the splash resolves the monitor work area,
    /// so the gate must hand back the saved restored rect either way.
    /// </summary>
    [Fact]
    public void MaximizedDoesNotChangeTheNormalizedRect()
    {
        Assert.True(WindowGeometryGate.TryNormalize(
            Geometry(x: 10, y: 20, w: 900, h: 700, maximized: true), out var rect));
        Assert.Equal((10, 20, 900, 700), rect);
    }
}
