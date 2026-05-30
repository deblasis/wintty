using Ghostty.Core.Hosting;
using Xunit;

namespace Ghostty.Tests.Hosting;

// Pure-logic coverage for the quake window's resize-edge direction math.
// Reference window rect is 1000 wide x 400 tall at the origin; grip is 8px.
public class QuickTerminalFrameGeometryTests
{
    private const int Left = 0, Top = 0, Right = 1000, Bottom = 400, Grip = 8;

    // ---- ResizableEdge: the edge opposite the dock ------------------------
    // One Fact rather than a Theory because the enum types are internal and
    // cannot appear as parameters of a public xUnit test method (CS0051).

    [Fact]
    public void ResizableEdge_Is_Opposite_The_Dock()
    {
        Assert.Equal(QuickTerminalResizeEdge.Bottom, QuickTerminalFrameGeometry.ResizableEdge(QuickTerminalPosition.Top));
        Assert.Equal(QuickTerminalResizeEdge.Top, QuickTerminalFrameGeometry.ResizableEdge(QuickTerminalPosition.Bottom));
        Assert.Equal(QuickTerminalResizeEdge.Right, QuickTerminalFrameGeometry.ResizableEdge(QuickTerminalPosition.Left));
        Assert.Equal(QuickTerminalResizeEdge.Left, QuickTerminalFrameGeometry.ResizableEdge(QuickTerminalPosition.Right));
        Assert.Equal(QuickTerminalResizeEdge.None, QuickTerminalFrameGeometry.ResizableEdge(QuickTerminalPosition.Center));
    }

    // ---- HitTest: only the resizable-edge strip reports a grip ------------

    [Fact]
    public void Top_Dock_Resizes_From_Bottom_Strip_Only()
    {
        // Inside the bottom grip strip [Bottom-Grip, Bottom].
        Assert.Equal(
            QuickTerminalResizeEdge.Bottom,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Top, Left, Top, Right, Bottom, 500, Bottom - 1, Grip));
        // Just above the strip -> client (no resize).
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Top, Left, Top, Right, Bottom, 500, Bottom - Grip - 1, Grip));
        // The docked (top) edge is NOT resizable.
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Top, Left, Top, Right, Bottom, 500, Top, Grip));
    }

    [Fact]
    public void Bottom_Dock_Resizes_From_Top_Strip_Only()
    {
        Assert.Equal(
            QuickTerminalResizeEdge.Top,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Bottom, Left, Top, Right, Bottom, 500, Top + 1, Grip));
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Bottom, Left, Top, Right, Bottom, 500, Top + Grip + 1, Grip));
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Bottom, Left, Top, Right, Bottom, 500, Bottom, Grip));
    }

    [Fact]
    public void Left_Dock_Resizes_From_Right_Strip_Only()
    {
        Assert.Equal(
            QuickTerminalResizeEdge.Right,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Left, Left, Top, Right, Bottom, Right - 1, 200, Grip));
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Left, Left, Top, Right, Bottom, Right - Grip - 1, 200, Grip));
    }

    [Fact]
    public void Right_Dock_Resizes_From_Left_Strip_Only()
    {
        Assert.Equal(
            QuickTerminalResizeEdge.Left,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Right, Left, Top, Right, Bottom, Left + 1, 200, Grip));
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Right, Left, Top, Right, Bottom, Left + Grip + 1, 200, Grip));
    }

    [Fact]
    public void Center_Never_Reports_A_Grip()
    {
        // No flush edge -> no custom resize strip anywhere.
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Center, Left, Top, Right, Bottom, 500, Bottom - 1, Grip));
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Center, Left, Top, Right, Bottom, Left, Top, Grip));
    }

    [Fact]
    public void HitTest_Honors_A_Non_Origin_Window_Rect()
    {
        // Window at (200, 100)-(1200, 500): bottom strip is y in [492, 500].
        Assert.Equal(
            QuickTerminalResizeEdge.Bottom,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Top, 200, 100, 1200, 500, 700, 495, Grip));
        Assert.Equal(
            QuickTerminalResizeEdge.None,
            QuickTerminalFrameGeometry.HitTest(QuickTerminalPosition.Top, 200, 100, 1200, 500, 700, 490, Grip));
    }
}
