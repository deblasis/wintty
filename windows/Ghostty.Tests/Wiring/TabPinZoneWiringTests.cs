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
    /// The commit classifies the crossing first, pins (or unpins) before
    /// it moves, and re-reads the row's index in between: SetPinned
    /// relocates the row, so a Move fired with the pre-pin index would
    /// land the row a slot off -- or get clamped into the zone it just
    /// left, which the refused-crossing break then turns into a dropped
    /// crossing for the rest of the gesture.
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

        // The flag's polarity is the crossing's direction. The argument
        // alone is only a variable name, so the initializer is what
        // actually goes red when the comparison inverts -- SetPinned(false)
        // on a crossing up would unpin the row mid-gesture.
        Assert.Equal("pin", setPinned.Arg(1));
        var flag = commit.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Single(l => l.Declaration.Variables.Any(v => v.Identifier.ValueText == "pin"));
        Assert.Equal(
            "zone.Op == TabPinZoneOp.Pin",
            flag.Declaration.Variables.Single().Initializer!.Value.ToString());

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
    /// The structural panel moved the pinned rows out of the scrolling
    /// list, so the zone edge is no longer a gap in the separator pool:
    /// it is the stroke along the shelf's bottom edge. The #808 semantics
    /// travel with it unchanged -- the manager's PinCount is the edge's
    /// truth at paint time, the stroke exists only while both zones do,
    /// it never depends on which row is active, and the drag gate is what
    /// brightens it.
    /// </summary>
    [Fact]
    public void TheBoundaryStroke_RidesTheShelf_NotTheRowPool()
    {
        var strip = Strip();

        // The pool draws only the ordinary row lines now. An accent stroke
        // left in it would paint a second boundary across a body-row gap,
        // at a slot where the zone does not end.
        var separators = strip.Method("UpdateRowSeparators");
        Assert.Empty(separators.Calls("BoundaryStrokeBrush"));

        // And it only walks the body rows: the pinned prefix renders above
        // the scroller and has no gaps in this pool to draw.
        var loop = separators.DescendantNodes().OfType<ForStatementSyntax>().Single();
        var start = loop.Declaration!.Variables.Single(v => v.Identifier.ValueText == "i");
        Assert.Equal("_manager.PinCount", start.Initializer!.Value.ToString());

        // The shelf refresh rides the separator pass: it is the one call
        // every selection-placement and drag entry/exit path already makes,
        // so the stroke's brighten/dim cannot be forgotten on an exit path.
        Assert.Single(separators.Calls("UpdatePinnedShelfChrome"));

        var shelf = strip.Method("UpdatePinnedShelfChrome");
        Assert.Single(shelf.Calls("BoundaryStrokeBrush"));

        // The shelf and the header exist only while pins do.
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

        // The stroke itself only while both zones do -- the same gate the
        // in-list boundary always had, all tabs pinned draws no edge.
        var bothZones = shelf.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Single(l => l.Declaration.Variables.Any(v => v.Identifier.ValueText == "bothZones"));
        Assert.Equal(
            "anyPins && _manager.PinCount < _manager.Tabs.Count",
            bothZones.Declaration.Variables.Single().Initializer!.Value.ToString());
        var strokeVisible = shelf.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_boundaryStroke.Visibility");
        Assert.Equal(
            "bothZones ? Visibility.Visible : Visibility.Collapsed",
            strokeVisible.Right.ToString());

        // The brightening is the gesture's aiming feedback, so its
        // polarity is load-bearing: dim while idle, bright while a drag
        // is live. Inverting it darkens the boundary exactly when the
        // gesture needs it.
        var brush = strip.Method("BoundaryStrokeBrush");
        var alpha = brush.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Single(l => l.Declaration.Variables.Any(v => v.Identifier.ValueText == "alpha"));
        Assert.Equal(
            "_drag is null ? (byte)0x59 : (byte)0xE6",
            alpha.Declaration.Variables.Single().Initializer!.Value.ToString());
    }

    /// <summary>
    /// Pointer drags announce nothing (5.6): the user is watching the row
    /// cross. The strip watches SetPinned happen all day and never speaks;
    /// the announcement hangs off the router, which is what makes a pin a
    /// commanded one (palette, chord, or context menu).
    /// </summary>
    [Fact]
    public void TheStrip_NeverAnnounces_AndTheWindowAnnouncesCommandedPins()
    {
        Assert.Empty(Strip().Root.Calls("UiaAnnouncer.Announce"));

        var window = ShellSource.Load("MainWindow.xaml.cs");
        var subscription = window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_router.TabPinChangedFromCommand");

        Assert.NotEmpty(subscription.Right.Calls("UiaAnnouncer.Announce"));
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
