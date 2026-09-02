using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pinned zone rides the PR 3 drag machine unchanged: the boundary is
/// index arithmetic in front of the commit, and everything the machine
/// already guarantees (arranged centers, velocity before the terminal
/// transition, the refused-crossing read-back) carries over. What is new
/// here is the one thing a wiring guard can actually see: a zone crossing
/// commits as SetPinned plus Move, in that order, with the truth read
/// back afterwards -- and a cancel puts the FLAGS back before it puts the
/// ORDER back, because the order is only expressible with the flags set.
///
/// Wiring guards, not behaviour tests: the boundary grammar's decisions
/// are pinned in TabPinBoundaryTests and the pairing in
/// TabManagerPinGroupTests. Whether the strip lands the row on the right
/// pixel is only observable on a live drag.
/// </summary>
public class TabPinZoneWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    /// <summary>
    /// The commit classifies the crossing first, pins before it moves,
    /// and re-reads the row's index in between: SetPinned relocates the
    /// row, so a Move fired with the pre-pin index would land the row a
    /// slot off -- or get clamped into the zone it just left, which the
    /// refused-crossing break then turns into a dropped crossing for the
    /// rest of the gesture. The arm PINS only: an Unpin classification
    /// mid-drag rewinds and refuses (the drag wiring fact pins that
    /// refusal), so the only flag this commit can pass is the literal
    /// true.
    /// </summary>
    [Fact]
    public void ZoneCrossing_CommitsAsSetPinnedThenMove_WithTheIndexReRead()
    {
        var commit = Strip().Method("EvaluateDrag");

        var setPinned = commit.Calls("_manager.SetPinned").Single();
        var move = commit.Calls("_manager.Move").Single();
        Assert.True(
            setPinned.Span.Start < move.Span.Start,
            "the zone crossing must SetPinned before Move: Move clamps into the "
            + "row's CURRENT zone, so moving first commits nothing");

        // The relocation changed where the row sits; the placement has to
        // start from where it actually ended up.
        Assert.True(
            commit.Calls("_manager.IndexOf").Any(
                call => call.Span.Start > setPinned.Span.Start
                        && call.Span.End < move.Span.Start),
            "the row's index must be re-read between SetPinned and Move");

        // The arm pins, literally. An argument that still names a computed
        // pin boolean would mean the gate widened back to "any zone
        // change" -- the shape that obeyed a mid-drag Unpin off stale
        // centers. The gate text itself is the drag wiring fact's pin.
        Assert.Equal("true", setPinned.Arg(1));

        // The zone commit repaints the boundary in the same tick: the
        // stroke is the gesture's aiming feedback and it just moved.
        // Painting it through UpdateSelectionRow or UpdateRowSeparators
        // alike, the pass has to follow the Move.
        var repaint = commit.Calls("UpdateRowSeparators").Single();
        Assert.True(
            repaint.Span.Start > move.Span.Start,
            "the boundary must be repainted after the zone commit lands");
    }

    /// <summary>
    /// The refused-crossing contract 3b landed applies to zone crossings
    /// too: a clamp that swallowed the placement updates the machine and
    /// BREAKS. A `continue` here would re-fire the identical refused
    /// crossing forever, because Evaluate is pure per tick. The read-back
    /// is manager truth (the crossing's translated destination, not the
    /// machine's slot) plus the row's post-commit slot resolvability --
    /// the machine speaks in visible slots, and a hidden collapsed member
    /// shifts manager indices without shifting slots, so the slot is
    /// re-derived from the fresh pairing, never the pre-commit one.
    /// </summary>
    [Fact]
    public void ARefusedCrossing_BreaksTheCommitLoop()
    {
        var refuse = Strip().Method("EvaluateDrag").DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "actual != managerTo || actualSlot < 0");

        Assert.True(
            refuse.Statement.DescendantNodesAndSelf().OfType<BreakStatementSyntax>().Any(),
            "the refused crossing must break out of the commit loop");
        Assert.True(
            !refuse.Statement.DescendantNodesAndSelf().OfType<ContinueStatementSyntax>().Any(),
            "a refused crossing that continues re-fires itself forever: Evaluate "
            + "is pure per tick, so nothing will have changed by the next pass");
    }

    /// <summary>
    /// A cancel that survived a mid-drag pin must restore the pin FLAGS
    /// before replaying the pre-drag order: Move clamps against the
    /// boundary the flags define, so order-first leaves the row stranded
    /// on the wrong side and the replay diverges from the state it is
    /// restoring.
    /// </summary>
    [Fact]
    public void Cancel_RestoresPinFlags_BeforeReplayingTheOrder()
    {
        var cancel = Strip().Method("CancelDrag");

        var restore = cancel.Calls("_manager.SetPinned").Single();
        var diff = cancel.Calls("TabStripProjection.Diff").Single();
        Assert.True(
            restore.Span.Start < diff.Span.Start,
            "flags must come home before the order diff is computed: SetPinned "
            + "relocates, so the diff has to run against the state the flags leave");

        // And they come home as they left: restoring the complement hands
        // every row the opposite flag and the replay diverges from the
        // state it is restoring.
        Assert.Equal("wasPinned", restore.Arg(1));
    }

    /// <summary>
    /// The band moved the pinned rows out of the scrolling list AND out
    /// of the business of drawing an edge: the zone is neither a gap in
    /// the separator pool nor a stroke under the shelf, it is a different
    /// shape stacked above one. The #808 semantics that survive are the
    /// ones about the pool -- it walks the body rows only, the manager's
    /// PinCount is where it starts, and the shelf refresh rides it so no
    /// exit path can forget the zone's chrome.
    /// </summary>
    [Fact]
    public void TheZoneEdge_IsTheBandsShape_NotAStroke()
    {
        var strip = Strip();

        // The pool draws only the ordinary row lines. An accent stroke
        // left in it would paint a boundary across a body-row gap, at a
        // slot where the zone does not end.
        var separators = strip.Method("UpdateRowSeparators");

        // And it only walks the body rows: the pinned prefix renders above
        // the scroller and has no gaps in this pool to draw.
        var loop = separators.DescendantNodes().OfType<ForStatementSyntax>().Single();
        var start = loop.Declaration!.Variables.Single(v => v.Identifier.ValueText == "i");
        Assert.Equal("_manager.PinCount", start.Initializer!.Value.ToString());

        // The shelf refresh rides the separator pass: it is the one call
        // every selection-placement and drag entry/exit path already makes,
        // so the band's chrome cannot be forgotten on an exit path.
        Assert.Single(separators.Calls("UpdatePinnedShelfChrome"));

        var shelf = strip.Method("UpdatePinnedShelfChrome");

        // The shelf exists only while pins do.
        var anyPins = shelf.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Single(l => l.Declaration.Variables.Any(v => v.Identifier.ValueText == "anyPins"));
        Assert.Equal(
            "_manager.PinCount > 0",
            anyPins.Declaration.Variables.Single().Initializer!.Value.ToString());
        var shelfVisible = shelf.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_pinnedShelf.Visibility");
        Assert.Equal(
            "anyPins ? Visibility.Visible : Visibility.Collapsed",
            shelfVisible.Right.ToString());

        // No "both zones" question survives: the band says where the zone
        // ends whether or not a body row exists below it, so a gate that
        // hid the edge when every tab was pinned has nothing left to hide.
        // (That the stroke itself is gone is PinnedPanelWiringTests'
        // TheBand_IsTheZoneAnchor; this is the gate it took with it.)
        Assert.DoesNotContain("bothZones", shelf.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The dispatch path announces; the state change never does (5.6).
    /// Pointer drags and the pointer chevron are watched as they land and
    /// speak nothing -- the strip performs the same manager ops all day and
    /// never calls the announcer. Pins and group commands both announce
    /// from MainWindow's router subscriptions: the router is what makes an
    /// op a commanded one.
    /// </summary>
    [Fact]
    public void TheStrip_StaysSilent_AndTheWindowAnnouncesCommandedPinsAndGroups()
    {
        Assert.Empty(Strip().Root.Calls("UiaAnnouncer.Announce"));

        var window = ShellSource.Load("MainWindow.xaml.cs");
        var subscriptions = window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "_router.TabPinChangedFromCommand"
                or "_router.GroupChangedFromCommand")
            .ToList();
        Assert.Equal(2, subscriptions.Count);
        foreach (var subscription in subscriptions)
            Assert.NotEmpty(subscription.Right.Calls("UiaAnnouncer.Announce"));

        // Group no-ops announce nothing: the router's guards sit before
        // every raise, so a same-state collapse or a rejoin is never
        // narrated as a change.
        var router = ShellSource.Load("Input.PaneActionRouter.cs");
        var collapse = router.Method("RequestCollapseGroup");
        var guard = collapse.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "group.IsCollapsed == collapsed");
        Assert.True(
            guard.Span.Start < collapse.Calls("GroupChangedFromCommand?.Invoke").Single().Span.Start,
            "the no-op guard must precede the announce raise");
    }

    /// <summary>
    /// The menu item toggles, and its label is re-evaluated on every open:
    /// a flyout built while the tab was unpinned can be opened after a
    /// drag pinned it, and a stale "Pin Tab" that pins a pinned tab is a
    /// command that lies. The click's polarity is the toggle itself.
    /// </summary>
    [Fact]
    public void ThePinMenuItem_Toggles_AndRelabelsOnOpen()
    {
        var build = ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Method("Build");

        var click = build.Calls("requestPin").Single();
        Assert.Equal("!tab.IsPinned", click.Arg(1));

        // The build-time label sits in the item's initializer as a bare
        // `Text = ...`; the re-evaluation is the one that names the item.
        var relabels = build.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "pin.Text")
            .ToList();
        Assert.True(
            relabels.Count == 1,
            $"expected exactly one relabel of the built item, found {relabels.Count}");
        Assert.True(
            relabels[0].Ancestors().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "flyout.Opening"),
            "the relabel must live in the Opening handler, not only at build time");
    }

    /// <summary>
    /// Sort Pinned joins the strip menu only while two pins exist to
    /// order; the manager no-ops below that, and an item that offers a
    /// no-op is chrome pretending to be a command.
    /// </summary>
    [Fact]
    public void SortPinned_IsOfferedOnlyWhileTwoPinsExist()
    {
        var build = ShellSource.Load("Tabs.StripContextMenuBuilder.cs").Method("Build");

        var gate = build.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "manager.PinCount > 1");
        Assert.Single(gate.Calls("manager.SortPinned"));
    }

    /// <summary>
    /// The case's job is the polarity; RequestPin's is the op and the
    /// event. The manager op is shared with the drag, the announcement is
    /// not, and the event is what carries that distinction to the window.
    /// </summary>
    [Fact]
    public void TheRouter_CommandsThePin_ThenRaisesTheCommandSourceEvent()
    {
        var source = ShellSource.Load("Input.PaneActionRouter.cs");

        // PinTab asks for pinned and UnpinTab for unpinned. Inverting the
        // comparison turns the palette's "Pin Tab" into an unpin while the
        // label still promises a pin.
        var section = source.Case("Invoke", "PaneAction.PinTab");
        Assert.Equal(
            "action == PaneAction.PinTab",
            section.Calls("RequestPin").Single().Arg(1));

        var pin = source.Method("RequestPin");
        var setPinned = pin.Calls("_tabs.SetPinned").Single();
        var raised = pin.Calls("TabPinChangedFromCommand?.Invoke").Single();
        Assert.True(
            setPinned.Span.Start < raised.Span.Start,
            "the event reports the state the tab was left in, so it must fire "
            + "after the manager op");

        // A command naming the state the tab already has did nothing, and
        // announcing it would narrate a change that never happened.
        Assert.Single(pin.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString() == "tab.IsPinned == pin"));
    }
}
