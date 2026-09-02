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

    // The raw strip source, for absence pins over things that are not
    // invocations -- an AddHandler hook is an argument, invisible to a
    // call scan. Same shape as VerticalTabGroupDragWiringTests.ReadStrip.
    private static string ReadStrip()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("VerticalTabStrip.xaml.cs", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

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

        // The slot promises where the square will actually land, and it is
        // ASKED of the band rather than derived here. A band wraps: the
        // next slot is sometimes beside the last square and sometimes at
        // the start of a new row, and arithmetic that assumes one pitch
        // below the last square is wrong at every column boundary -- which
        // is the regression this replaces, not merely a re-spelling of it.
        var slot = update.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "slot");
        var ask = Assert.IsType<InvocationExpressionSyntax>(slot.Initializer!.Value);
        Assert.Equal("BandSlotRect", ask.CalleeText());
        // One past the end: the slot the pin about to land will take.
        Assert.Equal("_manager.PinCount", ask.Arg(0));

        // The three named arguments, each pinned to ITS axis. Looking for
        // "slot.X" and "slot.Y" anywhere among the method's arguments is
        // satisfied by `top: slot.X, left: slot.Y` -- both tokens are still
        // present, and the ghost is drawn transposed. For a slot in band row
        // 1 column 0 that is top 0 left 44 instead of top 44 left 0, which
        // is a promise about a different square. The band's second axis is
        // the whole reason this guard exists: in a one-column stack a
        // transposition is invisible.
        var named = show.ArgumentList.Arguments
            .Where(a => a.NameColon is not null)
            .ToDictionary(a => a.NameColon!.Name.Identifier.ValueText, a => a.Expression.ToString());
        Assert.Equal("slot.Y", named["top"]);
        Assert.Equal("slot.X", named["left"]);
        Assert.Equal("slot.Width", named["width"]);
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
        var released = Strip().Method("DragRelease");

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
    /// family funnels through CancelDrag into it -- so one hide there
    /// covers the exit paths, and the fact ties each family to the funnel
    /// so a new exit path cannot quietly skip it.
    ///
    /// There is no capture-loss family, and the absence is pinned, not
    /// just skipped: the engine holds no capture, so no CaptureLost is
    /// ours -- the one the strip sees is MUXC's item layer releasing its
    /// own press capture the moment a drag starts moving, and acting on
    /// that murdered every real drag (the probe caught a cancel mid-drag,
    /// then a zombie crossing landing the right order by luck). The
    /// capture-less engine runs on hover-routed events; a PointerCapture
    /// Lost hook or a CapturePointer call re-appearing anywhere in the
    /// strip goes red here. The pointer-cancel family that DOES exist
    /// reaches the funnel through the wrapper's DragCancel, the hop the
    /// test-seam parameterization added.
    /// </summary>
    [Fact]
    public void EveryDragExit_ReachesTheHide()
    {
        var strip = Strip();

        Assert.Single(strip.Method("EndDrag").Calls("HidePinPreview"));

        // The cancel funnel ends at EndDrag, which is where the hide sits.
        Assert.NotEmpty(strip.Method("CancelDrag").Calls("EndDrag"));

        // Each exit family that exists: escape, pointer cancel (through
        // the parameterized core), the stale press that ends a session the
        // strip never saw the release of, the mid-drag row close, the
        // layout switch, and teardown.
        Assert.NotEmpty(strip.Method("OnDragKeyDown").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("OnDragPointerCanceled").Calls("DragCancel"));
        Assert.NotEmpty(strip.Method("DragCancel").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("DragPress").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("RemoveItem").Calls("CancelDrag"));
        Assert.NotEmpty(strip.Method("SetSelectionRowSuppressed").Calls("CancelDrag"));
        var unloaded = strip.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "Unloaded");
        Assert.NotEmpty(unloaded.Right.Calls("CancelDrag"));

        // And no capture, in either spelling: no PointerCaptureLost hook
        // for a family that must not come back, and no CapturePointer the
        // engine could hold. Text-level for the event name -- a hook is an
        // AddHandler argument, not an invocation -- and suffix-matched for
        // the call, which is only ever spelled with a receiver.
        Assert.DoesNotContain(
            "PointerCaptureLostEvent", ReadStrip(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            strip.Root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .ToList(),
            c => c.CalleeText().EndsWith("CapturePointer", StringComparison.Ordinal));

        // And the show runs only from the coalesced tick: a preview that
        // appeared from a raw pointer event would have no hide ordering
        // against the machine's phases.
        Assert.Single(strip.Method("EvaluateDrag").Calls("UpdatePinPreview"));
    }
}
