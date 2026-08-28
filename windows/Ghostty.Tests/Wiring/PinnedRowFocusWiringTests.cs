using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The shelf's keyboard story. The pinned rows sit outside MUXC, so the
/// body rows' focus traversal -- tab stops, arrows, selection-on-focus --
/// does not reach them by itself: the row carries the tab stop and the
/// peer that makes focus visible to a client, the strip's key handler
/// walks the shelf and crosses the boundary at its edges, and activation
/// takes the one fenced shelf seam. One seam into the shelf from the body
/// (Up from the first body row); every other arrow stays MUXC's.
///
/// Wiring guards, not behaviour tests: traversal feel is only observable
/// on a live strip; the manager-side pairing lives in TabManager tests.
/// </summary>
public class PinnedRowFocusWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    private static ShellSource Row() =>
        ShellSource.Load("Tabs.VerticalTabPinnedRow.cs");

    private static ShellSource Peer() =>
        ShellSource.Load("Accessibility.VerticalTabPinnedRowAutomationPeer.cs");

    /// <summary>
    /// A shelf row must be reachable at all: the tab stop is what lets it
    /// take focus, and the peer is what reports that focus to a client --
    /// a plain Grid gets no peer, so without the override a screen reader
    /// never hears that the shelf is where focus went. The peer types the
    /// row ListItem (what MUXC types the body rows) and claims
    /// focusability, or keyboard traversal skips the shelf entirely.
    /// </summary>
    [Fact]
    public void ShelfRows_AreTabStops_AndReportFocusToClients()
    {
        var ctor = Row().Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "VerticalTabPinnedRow");
        var tabStop = ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "IsTabStop");
        Assert.Equal("true", tabStop.Right.ToString());

        // The peer exists, and says ListItem + focusable. Flipping the
        // control type or the focusability core is the "shelf is
        // invisible to clients" regression.
        Assert.Contains(
            Row().Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.ValueText == "OnCreateAutomationPeer");
        var peer = Peer();
        var controlType = peer.Method("GetAutomationControlTypeCore");
        Assert.Contains(
            "AutomationControlType.ListItem",
            controlType.ExpressionBody!.ToString());
        var focusable = peer.Method("IsKeyboardFocusableCore");
        Assert.Contains("IsTabStop: true", focusable.ExpressionBody!.ToString());
    }

    /// <summary>
    /// Enter and Space activate through the ONE shelf seam -- the same
    /// fenced activation a shelf click takes -- and nothing else: no
    /// selection write, no unfenced manager call. The fence matters
    /// because Activate can surface a selection change synchronously, and
    /// an unfenced one re-enters as a choice the user did not make. The
    /// active-row guard keeps re-activating the active tab from repainting
    /// the strip for no state change.
    /// </summary>
    [Fact]
    public void EnterOrSpace_ActivatesThroughTheSharedFence()
    {
        var key = Strip().Method("OnPinnedRowKeyDown");

        // Both keys, one arm, and it is the shared seam -- not a private
        // Activate call that could drift from the click path.
        var activate = key.Calls("ActivateFromShelf").Single();
        Assert.Equal("tab", activate.Arg(0));
        var arm = activate.Ancestors().OfType<SwitchSectionSyntax>().First();
        Assert.Contains(
            "Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space",
            arm.Labels.ToString());
        var handled = arm.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "e.Handled")
            .ToList();
        Assert.NotEmpty(handled);

        // The seam itself: guarded, fenced, then the manager call.
        var seam = Strip().Method("ActivateFromShelf");
        var guard = seam.DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.Equal(
            "ReferenceEquals(tab, _manager.ActiveTab)",
            guard.Condition.ToString());
        var managerActivate = seam.Calls("_manager.Activate").Single();
        Assert.Equal("tab", managerActivate.Arg(0));
        var fence = seam.AssignsTo("_syncing")
            .Where(a => a.Right.ToString() == "true")
            .ToList();
        Assert.Single(fence);
        Assert.True(fence[0].Span.Start < managerActivate.Span.Start,
            "the activation must be fenced, not just performed");

        // And the click path shares it: one seam, two callers.
        Assert.NotEmpty(Strip().Method("OnDragPointerReleased")
            .Calls("ActivateFromShelf"));
    }

    /// <summary>
    /// Arrows cross the boundary only at its edges. Down past the last
    /// pinned row lands on the FIRST body row, where MUXC's traversal
    /// resumes; inside the shelf the neighbours are the panel's own
    /// children, whose order the reconcile keeps equal to the projection.
    /// Up from the first body row is the one seam INTO the shelf, and it
    /// exists only while the shelf does -- without the pin gate the
    /// interception would fork MUXC's traversal on every pin-free strip.
    /// </summary>
    [Fact]
    public void Arrows_CrossTheBoundary_AtItsEdges_Only()
    {
        var strip = Strip();

        // Out of the shelf downward, and only downward: the neighbour walk
        // exits to the first body row on the DOWN edge alone.
        var walk = strip.Method("FocusShelfNeighbour");
        var exits = walk.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith(".Focus"))
            .ToList();
        Assert.Equal(2, exits.Count);
        var bodyExit = exits.Single(c => c.CalleeText() == "firstBody.Focus");
        var bodyExitGate = bodyExit.Ancestors().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("delta > 0"));
        Assert.Contains("firstBody.Focus", bodyExitGate.Statement.ToString());
        // The top edge is a stop, spelled as the bare false it returns.
        Assert.Contains(walk.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression!.ToString() == "false");

        // Into the shelf: Up from the FIRST body row only, and only while
        // the shelf exists. First though, the stand-down the shelf handler
        // already keeps: while a drag is live the keyboard belongs to it
        // (Escape cancels), so the body seam must not fire under one.
        var body = strip.Method("OnBodyRowKeyDown");
        var standDown = body.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Equal("_drag is not null", standDown.Condition.ToString());
        Assert.Contains(
            standDown.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
            r => true);
        var upGate = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "e.Key != Windows.System.VirtualKey.Up");
        Assert.Contains(
            upGate.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
            r => true);
        var pinGate = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_manager.PinCount == 0");
        Assert.Contains(
            pinGate.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
            r => true);

        // The target is the LAST shelf row -- the geometric neighbour --
        // and the key is claimed only after focus actually landed.
        var focus = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText().Contains("_pinnedPanel.Children[^1].Focus"));
        Assert.Equal("FocusState.Programmatic", focus.Arg(0));
        var claimed = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "e.Handled");
        Assert.True(focus.Span.Start < claimed.Span.Start,
            "the key may be claimed only after focus landed on the shelf");
    }

    /// <summary>
    /// The wiring is attached where the rows are built, so a row that
    /// exists is a row that listens: the shelf rows get the key handler
    /// and the focus visual, and the body items get the boundary seam --
    /// each on its own container.
    /// </summary>
    [Fact]
    public void TheHandlers_AreWiredWhereTheRowsAreBuilt()
    {
        var strip = Strip();

        var pinned = strip.Method("AddPinnedRow");
        var pinnedWiring = pinned.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "row.KeyDown" or "row.GotFocus" or "row.LostFocus")
            .Select(a => a.Left.ToString())
            .ToList();
        Assert.Equal(
            new[] { "row.KeyDown", "row.GotFocus", "row.LostFocus" }, pinnedWiring);

        var bodyItem = strip.Method("AddBodyRow");
        var bodyWiring = bodyItem.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "item.KeyDown")
            .ToList();
        Assert.Single(bodyWiring);

        // The focus visual is the pane's hover-fill resource, resolved
        // through the strip's theme resources -- not a second colour
        // invented for focus. Pinned rows paint no hover state today, so
        // this fill is focus's alone.
        var visual = strip.Method("OnPinnedRowFocusVisual");
        Assert.Single(visual.Calls("ResolveThemeBrush"));
    }

    /// <summary>
    /// Focus survives the churn this strip rebuilds with. Making shelf rows
    /// tab stops made them removable while focused: a keyboard unpin (or
    /// any zone crossing) surfaces as Remove+Add, the removed element takes
    /// focus with it, and the rebuilt one starts unfocused -- without a
    /// hand-off the focus drops out of the strip entirely and the arrows
    /// go dead until a click. The hand-off is polarity-guarded at both
    /// ends: RemoveItem records the candidate only when the removed
    /// element actually held focus (a rebuild that churns an unfocused row
    /// must restore nothing), and AddItem restores only to the same tab's
    /// fresh element, forgetting the candidate before it focuses so a
    /// later unrelated churn cannot re-fire a stale one.
    /// </summary>
    [Fact]
    public void AFocusedRow_RebuiltByChurn_TakesFocusWithIt()
    {
        var strip = Strip();

        // Both removal arms record, and only when the row held focus: the
        // exact texts are the polarity. Inverting either comparison makes
        // the record fire for unfocused rows instead of focused ones.
        var removes = strip.Method("RemoveItem").AssignsTo("_refocusTab").ToList();
        Assert.Equal(2, removes.Count);
        Assert.Equal(
            new[]
            {
                "pinned.FocusState != FocusState.Unfocused",
                "item.FocusState != FocusState.Unfocused",
            },
            removes.Select(a => a.Ancestors().OfType<IfStatementSyntax>().First()
                .Condition.ToString()).ToArray());
        Assert.All(removes, a => Assert.Equal("tab", a.Right.ToString()));

        // The restore: gated on the same tab, and the candidate is dropped
        // before the focus lands -- gate, forget, focus, in that order, or
        // a churn of another tab could re-fire this one.
        var add = strip.Method("AddItem");
        var gate = add.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!ReferenceEquals(_refocusTab, tab)");
        Assert.Contains(
            gate.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
            r => true);
        var forget = add.AssignsTo("_refocusTab").Single();
        Assert.Equal("null", forget.Right.ToString());
        var focus = add.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "RowElementOf(tab)?.Focus");
        Assert.Equal("FocusState.Programmatic", focus.Arg(0));
        Assert.True(
            gate.Span.End < forget.Span.Start && forget.Span.Start < focus.Span.Start,
            "the restore must gate, then forget, then focus");
    }
}
