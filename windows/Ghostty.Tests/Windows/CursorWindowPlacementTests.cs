using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class CursorWindowPlacementTests
{
    private static readonly WorkAreaRect WorkArea = new(X: 0, Y: 0, Width: 1920, Height: 1080);

    [Fact]
    public void CursorAtOrigin_PlacesAtCursorMinusOffset_ClampedIntoWorkArea()
    {
        // Offset is 32,32. Cursor (0,0) minus offset is (-32,-32).
        // Clamp pulls X and Y back to the work area top-left.
        var rect = CursorWindowPlacement.Compute(
            cursorX: 0, cursorY: 0,
            windowWidth: 800, windowHeight: 600,
            workArea: WorkArea);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(800, rect.Width);
        Assert.Equal(600, rect.Height);
    }

    [Fact]
    public void CursorInInterior_PlacesWithOffset()
    {
        var rect = CursorWindowPlacement.Compute(
            cursorX: 500, cursorY: 400,
            windowWidth: 800, windowHeight: 600,
            workArea: WorkArea);

        // 500 - 32 = 468, 400 - 32 = 368.
        Assert.Equal(468, rect.X);
        Assert.Equal(368, rect.Y);
    }

    [Fact]
    public void CursorNearRightEdge_ClampsSoWindowFitsInsideWorkArea()
    {
        // cursor at x=1900, window 800 wide: raw X would be 1868,
        // right edge = 2668, outside work area (right=1920). Clamp
        // so right edge == 1920: X = 1920 - 800 = 1120.
        var rect = CursorWindowPlacement.Compute(
            cursorX: 1900, cursorY: 500,
            windowWidth: 800, windowHeight: 600,
            workArea: WorkArea);

        Assert.Equal(1120, rect.X);
    }

    [Fact]
    public void CursorNearBottomEdge_ClampsSoWindowFitsInsideWorkArea()
    {
        var rect = CursorWindowPlacement.Compute(
            cursorX: 500, cursorY: 1070,
            windowWidth: 800, windowHeight: 600,
            workArea: WorkArea);

        // raw Y = 1038, bottom = 1638 > 1080. Clamp: Y = 1080 - 600 = 480.
        Assert.Equal(480, rect.Y);
    }

    [Fact]
    public void WindowLargerThanWorkArea_AlignsTopLeftToWorkArea()
    {
        // If the requested window is bigger than the work area, we
        // can't fit it. Contract: pin top-left to work area origin
        // and let the system clip.
        var rect = CursorWindowPlacement.Compute(
            cursorX: 500, cursorY: 500,
            windowWidth: 3000, windowHeight: 2000,
            workArea: WorkArea);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(3000, rect.Width);
        Assert.Equal(2000, rect.Height);
    }

    [Fact]
    public void SecondaryMonitor_UsesWorkAreaOrigin()
    {
        // Secondary monitor to the right at (1920,0) 1920x1080.
        var secondary = new WorkAreaRect(X: 1920, Y: 0, Width: 1920, Height: 1080);
        var rect = CursorWindowPlacement.Compute(
            cursorX: 2500, cursorY: 500,
            windowWidth: 800, windowHeight: 600,
            workArea: secondary);

        // raw = (2468, 468); both inside secondary.
        Assert.Equal(2468, rect.X);
        Assert.Equal(468, rect.Y);
    }
}
