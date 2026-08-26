using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A tab's terminal area carries two frames: one around the whole area,
/// which never moves, and one around whichever leaf has focus.
///
/// The outer one is not decoration. The selected tab is drawn as a folder
/// whose fill runs into the terminal, and the seam cover erases the pane
/// border for exactly the tab's span so no line is ruled between them.
/// That only works if a border is there to erase, under the strip,
/// whatever the focus is doing -- which is what the tab frame guarantees
/// and what framing the active leaf alone did only by coincidence.
///
/// These are wiring guards. Whether the join actually reads as continuous
/// is only observable on a live window; what they pin is that the frame is
/// unconditional, is coloured from the same value as the focus frame, and
/// that the two never stroke the same rectangle twice.
/// </summary>
public class PaneChromeFramesWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Panes.PaneHost.cs");

    private static System.Collections.Generic.List<InvocationExpressionSyntax> AddsTo(
        ShellSource source, string container, string child) =>
        source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == container + ".Children.Add"
                        && i.ArgumentList.Arguments.Count == 1
                        && i.Arg(0) == child)
            .ToList();

    private static int ZIndexOf(ShellSource source, string element) =>
        int.Parse(source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "Canvas.SetZIndex" && i.Arg(0) == element)
            .Arg(1));

    /// <summary>
    /// Every path that mounts the highlight overlay mounts the tab frame
    /// too. Stated against the overlay rather than as a count, so a third
    /// constructor cannot pick up the focus chrome and quietly leave the
    /// tab with nothing for its folder to join to.
    /// </summary>
    [Fact]
    public void TabFrame_IsMounted_WhereverTheHighlightOverlayIs()
    {
        var host = Host();

        var overlay = AddsTo(host, "hostGrid", "_highlightOverlay");
        Assert.True(overlay.Count >= 2, "expected the overlay mounted from both constructors");
        Assert.Equal(overlay.Count, AddsTo(host, "hostGrid", "_tabContentBorderFrame").Count);
    }

    /// <summary>
    /// Not a child of the overlay. Zoom collapses the overlay wholesale,
    /// and a tab frame inside it would come down with it -- leaving the
    /// selected tab joined to nothing for as long as a pane is zoomed,
    /// which is the same hole splitting used to open.
    /// </summary>
    [Fact]
    public void TabFrame_DoesNotLiveOnTheOverlayThatZoomCollapses()
    {
        Assert.Empty(AddsTo(Host(), "_highlightOverlay", "_tabContentBorderFrame"));
    }

    /// <summary>
    /// The frame has no Visibility of its own anywhere in the file. Every
    /// state that could plausibly hide it -- no split, a zoom, a pane
    /// being closed -- is a state where the tab is still joined to the
    /// strip and still needs the line.
    /// </summary>
    [Fact]
    public void TabFrame_IsUnconditional()
    {
        var writes = Host().Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_tabContentBorderFrame.Visibility")
            .ToList();

        Assert.True(
            writes.Count == 0,
            $"the tab frame must always be drawn, found {writes.Count} Visibility write(s)");
    }

    /// <summary>
    /// One brush reaches both frames. A per-tab preset colour that reached
    /// only the focus frame would draw a tab stroked in the preset closing
    /// onto a pane framed in the cursor colour.
    /// </summary>
    [Fact]
    public void BothFrames_TakeTheSameBrush()
    {
        var body = Host().Method("SetActiveBorderBrush").Body!.Statements;

        var assignments = body.SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".BorderBrush", System.StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, assignments.Count);
        Assert.Contains(assignments, a => a.Left.ToString() == "_activeBorderFrame.BorderBrush");
        Assert.Contains(assignments, a => a.Left.ToString() == "_tabContentBorderFrame.BorderBrush");
        Assert.True(
            assignments[0].Right.ToString() == assignments[1].Right.ToString(),
            "both frames must take the same resolved brush, not two separately derived ones");
    }

    /// <summary>
    /// The focus frame stands down where it would restroke the tab frame.
    /// Same colour, same thickness, same rectangle: the second stroke does
    /// not vanish into the first, it doubles the antialiased edge and the
    /// frame around a single-pane tab comes out heavier than the frame
    /// around a split one.
    /// </summary>
    [Fact]
    public void ActiveFrame_StandsDown_WhereItWouldRestrokeTheTabFrame()
    {
        var body = Host().Method("PositionActiveBorderOverLeaf").Body!.Statements;

        var guard = body
            .TakeWhile(s => !s.Calls("Core.Panes.PaneChrome.LeafFillsContent").Any())
            .Count();
        Assert.True(
            guard < body.Count,
            "PositionActiveBorderOverLeaf must ask PaneChrome whether the tab frame "
            + "already draws this rectangle");

        var show = body
            .TakeWhile(s => !s.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "_activeBorderFrame.Visibility"
                          && a.Right.ToString() == "Visibility.Visible"))
            .Count();
        Assert.True(show < body.Count, "expected the frame to be shown somewhere in the method");
        Assert.True(
            guard < show,
            "the coincidence check has to run before the frame is shown, or the second "
            + "stroke is drawn and then left up");
    }

    /// <summary>
    /// Drawn over the highlight overlay, and under the restore-from-zoom
    /// button. The overlay carries the inactive-pane dim film, which runs
    /// to those panes' outer edges -- the tab frame's own edges -- so
    /// beneath it the frame is darkened for exactly the stretch an
    /// unfocused pane occupies.
    /// </summary>
    [Fact]
    public void TabFrame_DrawsOverTheDimFilmAndUnderTheZoomAffordance()
    {
        var host = Host();

        var overlay = ZIndexOf(host, "_highlightOverlay");
        var frame = ZIndexOf(host, "_tabContentBorderFrame");
        var restore = ZIndexOf(host, "_restoreZoomButton");

        Assert.True(frame > overlay, $"tab frame z={frame} must beat the overlay z={overlay}");
        Assert.True(restore > frame, $"restore button z={restore} must beat the tab frame z={frame}");
    }
}
