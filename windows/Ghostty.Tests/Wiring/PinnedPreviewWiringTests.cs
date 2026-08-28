using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pin drop preview (5.5): an icon-only ghost slot promising where a
/// body row dragged over the shelf would land. Three load-bearing
/// properties are pinned here. The promise's polarity -- it exists only
/// while the drop would actually deliver a pin, because a ghost that
/// outlives its premise is a lie about where the row will end up. Its
/// blindness to the manager -- the ghost is pixels, and the commit is the
/// zone grammar's job. And the exit completeness -- every way a drag ends
/// takes the ghost with it, or a promise outlives the gesture.
///
/// Wiring guards, not behaviour tests: where the ghost paints on a live
/// drag is only observable on a live strip.
/// </summary>
public class PinnedPreviewWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    /// <summary>
    /// Show means "the drop would pin": the dragged row is still unpinned,
    /// pins exist, and its center is over the shelf. The pinned arm of the
    /// gate is the one that goes wrong silently -- once the crossing has
    /// committed, the real icon-only row is in the shelf following the
    /// pointer, and a ghost alongside it promises a slot the real row
    /// already holds. The shelf-bottom comparison is the "over the shelf"
    /// half of the grammar, in the coordinates the machine judges
    /// crossings in.
    /// </summary>
    [Fact]
    public void ThePreview_ShowsOnlyWhileTheDropWouldPin()
    {
        var update = Strip().Method("UpdatePinPreview");

        // The gate hides on every non-promise state, with the pinned test
        // first: flipping it to `!drag.Tab.IsPinned` keeps the ghost up
        // after the commit, which is the double-promise regression -- so
        // the disjunct is pinned at the head of the condition, polarity
        // included.
        var gate = update.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("drag.Tab.IsPinned"));
        Assert.StartsWith(
            "drag.Tab.IsPinned || _manager.PinCount == 0",
            gate.Condition.ToString());
        Assert.Contains("draggedCenter >= shelfBottom", gate.Condition.ToString());
        Assert.Contains(
            gate.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "HidePinPreview");

        // And the hide short-circuits the show: no branch past the gate
        // may run while the gate holds.
        var show = update.Calls("ShowPinPreview").Single();
        Assert.True(gate.Span.End < show.Span.Start,
            "the show must be unreachable while the hide-gate holds");

        // The slot promises where the row will actually land: one row pitch
        // down from the last pinned row's center, with BOTH of the rows'
        // 2px vertical insets accounted. The ghost zeroes its own margin
        // and a real row's top margin is half of where its center lands,
        // so shorting either inset parks the ghost 2px proud of the slot
        // and flashes at the handoff. The exact expression is the guard --
        // the omission, not an inversion, is how this regresses.
        Assert.Contains(
            update.DescendantNodes().OfType<ArgumentSyntax>(),
            a => a.Expression.ToString() ==
                "lastCenter + VerticalTabPinnedRow.RowHeight / 2 "
                + "+ 2 * RowInsetVertical");
    }

    /// <summary>
    /// An unreadable measurement hides the ghost rather than moving it.
    /// EvaluateDrag returns early when the dragged row has no arranged
    /// truth this tick, which used to skip UpdatePinPreview entirely -- a
    /// ghost left standing on the previous tick's promise. The early
    /// return must hide first, and it must sit before the tick's show
    /// call, so "no promise" is what an unreadable frame delivers.
    /// </summary>
    [Fact]
    public void AnUnreadableMeasurement_HidesThePreview()
    {
        var evaluate = Strip().Method("EvaluateDrag");
        var unreadable = evaluate.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "double.IsNaN(arranged)");
        Assert.Contains(
            unreadable.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "HidePinPreview");
        Assert.Contains(
            unreadable.Statement.DescendantNodesAndSelf()
                .OfType<ReturnStatementSyntax>(),
            r => true);

        // And the hide comes before the tick's show, so a tick either
        // commits to a fresh promise or commits to none.
        var show = evaluate.Calls("UpdatePinPreview").Single();
        Assert.True(unreadable.Span.End < show.Span.Start,
            "the unreadable-measurement hide must precede the tick's show");
    }

    /// <summary>
    /// The ghost is strictly visual. None of the three state-machine
    /// methods may touch the manager: the commit at drop flows through
    /// Classify/SetPinned in the release path, and a preview that moved a
    /// row would turn a promise into a mutation a cancel could not undo.
    /// The ghost also never joins the pinned panel -- the reconcile counts
    /// those children against the projection, and a ghost child would
    /// fail every mid-drag commit into a full rebuild.
    /// </summary>
    [Fact]
    public void ThePreview_IsPixelsOnly_NeverManagerState()
    {
        foreach (var method in new[]
                 {
                     "UpdatePinPreview", "ShowPinPreview", "HidePinPreview",
                 })
        {
            var body = Strip().Method(method);
            Assert.DoesNotContain(
                body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                c => c.CalleeText().StartsWith("_manager."));
            Assert.DoesNotContain(
                body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                c => c.CalleeText() == "_pinnedPanel.Children.Add");
        }

        // The ghost lives in the overlay host instead, which no reconcile
        // counts.
        var show = Strip().Method("ShowPinPreview");
        Assert.Single(show.Calls("PreviewHost.Children.Add"));
    }

    /// <summary>
    /// The drop honours the promise the user can see, through the same
    /// zone grammar the tick loop commits with: the ghost's visibility is
    /// the gate, Classify names the op, and SetPinned relocates the row to
    /// the prefix's last slot -- exactly where the ghost sat, so the hand
    /// off shows no flash of wrong position. Committing without the gate
    /// would pin rows dropped anywhere; committing without the grammar
    /// would bypass the read-back.
    /// </summary>
    [Fact]
    public void TheDrop_HonoursTheVisibleGhost_ThroughTheZoneGrammar()
    {
        var released = Strip().Method("OnDragPointerReleased");

        var gate = released.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_pinPreview is not null");
        var classify = gate.Calls("TabPinBoundary.Classify").Single();
        var setPinned = gate.Calls("_manager.SetPinned").Single();
        Assert.True(classify.Span.Start < setPinned.Span.Start,
            "the drop commit must classify before it pins, like the tick loop");

        Assert.Equal("drag.Tab.IsPinned", classify.Arg(0));
        Assert.Equal("_manager.PinCount - 1", classify.Arg(3));
        Assert.Equal("drag.Tab", setPinned.Arg(0));
        Assert.Equal("true", setPinned.Arg(1));

        // The churn replaced the dragged row's element, so there is no
        // live visual to settle: this path must land the row as a cut,
        // not start a spring on a visual that is no longer in the tree.
        var end = gate.Calls("EndDrag").Single();
        Assert.Equal("settle: false", end.Arg(1));
    }

    /// <summary>
    /// Every way a drag ends takes the ghost with it. EndDrag is the
    /// shared tail -- the release drops through it, and every cancel
    /// family (escape, capture loss, row close, layout switch, teardown)
    /// funnels through CancelDrag into it -- so one hide there covers the
    /// exit paths, and the fact ties each family to the funnel so a new
    /// exit path cannot quietly skip it.
    /// </summary>
    [Fact]
    public void EveryDragExit_ReachesTheHide()
    {
        var strip = Strip();

        Assert.Single(strip.Method("EndDrag").Calls("HidePinPreview"));

        // The cancel funnel ends at EndDrag, which is where the hide sits.
        Assert.NotEmpty(strip.Method("CancelDrag").Calls("EndDrag"));

        // Each exit family: escape, pointer cancel, capture loss, the
        // mid-drag row close, the layout switch, and teardown.
        Assert.NotEmpty(strip.Method("OnDragKeyDown").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("OnDragPointerCanceled").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("OnDragPointerCaptureLost").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("RemoveItem").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("SetSelectionRowSuppressed").Calls("CancelDrag"));
        var unloaded = strip.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "Unloaded");
        Assert.NotEmpty(unloaded.Right.Calls("CancelDrag"));

        // And the show runs only from the coalesced tick: a preview that
        // appeared from a raw pointer event would have no hide ordering
        // against the machine's phases.
        Assert.Single(strip.Method("EvaluateDrag").Calls("UpdatePinPreview"));
    }
}
