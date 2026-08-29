using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The strip-order bridge between <c>TabManager</c> and the two strip
/// hosts: the drag lifecycle, the reconcile, the vertical rebuild. The
/// shell cannot load into this test host, so these guards parse it; the
/// behaviour itself is tested outright in TabStripProjectionTests. What
/// a parse adds is the polarity each route needs.
/// </summary>
public sealed class TabStripSyncWiringTests
{
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";
    private const string TabHostXaml = "Tabs.TabHost.xaml";
    private const string VerticalSource = "Tabs.VerticalTabStrip.xaml.cs";

    // --- MoveItem: the no-op guard ---

    [Fact]
    public void MoveItem_declines_an_item_already_at_its_target_before_touching_the_strip()
    {
        var moveItem = ShellSource.Load(TabHostSource).Method("MoveItem");

        // The live index comes off the strip, not out of the event: the
        // event's indices are the raw op's, which TabView has usually
        // applied already by the time this handler runs.
        var liveIndex = moveItem.DescendantNodes()
            .OfType<EqualsValueClauseSyntax>()
            .Select(v => v.Value)
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("TabItems.IndexOf", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            liveIndex.Count == 1,
            $"MoveItem should read the item's index from TabItems once; found {liveIndex.Count}.");

        // The event's index counts tabs; the strip's slots also hold
        // chips. The raw `to` never reaches a comparison with a strip
        // index -- it crosses the projection's forward mapping first,
        // which is the polarity the in-place guard below depends on.
        Assert.True(
            moveItem.Calls("TabStripProjection.ModelIndexToVisibleIndex").Count == 1,
            "MoveItem must translate the event's raw index through the " +
            "projection's forward mapping; a raw tab index is not a slot index.");

        var firstMutation = moveItem.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));

        var guard = moveItem.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsAlreadyInPlaceGuard)
            .ToList();
        Assert.True(
            guard.Count == 1 && guard[0].SpanStart < firstMutation.SpanStart,
            "MoveItem must decline an in-place item before its first TabItems.Remove; "
            + "equality on purpose, since the inverted form skips the move it exists to skip.");

        // An unbraced if body IS the return statement rather than a block
        // containing one, so accept either shape.
        var body = guard.Count == 1 ? guard[0].Statement : null;
        var bailed = body is ReturnStatementSyntax { Expression: null }
            || body?.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression is null) == true;
        Assert.True(bailed, "The in-place guard must return, not fall through to the move.");
    }

    [Fact]
    public void MoveItem_lets_the_projector_own_the_final_word()
    {
        var moveItem = ShellSource.Load(TabHostSource).Method("MoveItem");

        // TabMoved carries the raw op's indices and Normalize may have
        // relocated tabs after it, so no path through MoveItem is ever
        // the last writer.
        var firstMutation = moveItem.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));
        var guard = moveItem.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsAlreadyInPlaceGuard)
            .ToList();
        var reconcile = moveItem.Calls("ReconcileStripOrder").ToList();

        Assert.True(
            reconcile.Count == 2,
            "MoveItem must reconcile on BOTH paths: an in-place index for the moved "
            + "item does not mean Normalize did not repair the rest of the strip "
            + $"(group re-gather). Found {reconcile.Count}; one call means a path returns early.");
        Assert.True(
            guard.Count == 1 && reconcile.Any(r =>
                r.SpanStart > guard[0].SpanStart && r.Span.End < guard[0].Span.End),
            "The in-place early return must run ReconcileStripOrder before returning.");
        Assert.True(
            reconcile.Any(r => r.SpanStart > firstMutation.SpanStart),
            "The moved path must reconcile after its own mutations.");
    }

    // --- Drag lifecycle: seam cover and the drop reconcile ---

    [Fact]
    public void Drag_start_hides_the_seam_cover_and_the_drop_replaces_it()
    {
        var src = ShellSource.Load(TabHostSource);
        var starting = src.Method("OnTabDragStarting");
        var completed = src.Method("OnTabDragCompleted");

        Assert.True(
            SetsFlag(starting, "_stripDragActive", SyntaxKind.TrueLiteralExpression),
            "OnTabDragStarting must raise _stripDragActive (audit 3.2, item 1).");
        Assert.True(
            SetsFlag(completed, "_stripDragActive", SyntaxKind.FalseLiteralExpression),
            "OnTabDragCompleted must lower _stripDragActive so the cover is placed again.");

        // Synchronous, not queued: a dispatcher hop paints one frame
        // against the stale slot. Nothing else runs between the flag
        // going up and the drag, so deleting the hide must fail here.
        var hide = starting.Calls("SelectedTabSeamChanged?.Invoke").ToList();
        Assert.True(
            hide.Count == 1
                && hide[0].Arg(0) == "0" && hide[0].Arg(1) == "0" && hide[0].Arg(2) == "null",
            "OnTabDragStarting must hide the cover synchronously with (0, 0, null).");

        // Wired even though TabHost.xaml predates it: the drag start is
        // where the stale-slot window opens.
        var ctor = src.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");
        var hooked = ctor.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                        && a.Left.ToString().EndsWith("TabDragStarting", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            hooked.Count == 1,
            "The constructor must subscribe OnTabDragStarting on TabViewControl.TabDragStarting.");
    }

    [Fact]
    public void The_seam_cover_stays_down_while_a_drag_holds_the_strip()
    {
        var bridge = ShellSource.Load(TabHostSource).Method("UpdateSelectedTabBridge");

        // The gate reads the flag bare and hides the cover itself, ahead
        // of the placement math. The condition is matched as a parsed bare
        // identifier, not a substring: the inverted gate mentions the same
        // flag but parses as a prefix unary expression.
        var gate = bridge.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is IdentifierNameSyntax id
                        && id.Identifier.ValueText == "_stripDragActive")
            .ToList();
        Assert.True(
            gate.Count == 1,
            "UpdateSelectedTabBridge needs one bare `if (_stripDragActive)` gate.");

        var placement = bridge.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().Contains("TransformToVisual", StringComparison.Ordinal));
        var hide = gate.Count == 1
            ? gate[0].Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(c => c.CalleeText() == "SelectedTabSeamChanged?.Invoke")
            : null;
        Assert.True(
            hide is not null
                && hide.Arg(0) == "0" && hide.Arg(1) == "0" && hide.Arg(2) == "null"
                && gate[0].SpanStart < placement.SpanStart,
            "The drag gate must hide the cover with (0, 0, null), ahead of the placement math.");
    }

    [Fact]
    public void The_drop_reconciles_even_when_the_manager_refused_the_move()
    {
        var completed = ShellSource.Load(TabHostSource).Method("OnTabDragCompleted");

        var reconcile = completed.Calls("ReconcileStripOrder").ToList();
        Assert.True(
            reconcile.Count == 1,
            "OnTabDragCompleted must reconcile the strip to the manager's order.");
        Assert.Empty(reconcile[0].Ancestors().OfType<IfStatementSyntax>());

        // The refused drop raises no TabMoved, so nothing else would run
        // the reconcile: a clamp against the pin boundary (PR 4's grammar)
        // leaves TabView's own reorder standing unless the reconcile is
        // unconditional.
    }

    // --- The reconcile is defensive at the UI boundary ---

    [Fact]
    public void A_reconcile_failure_logs_and_rebuilds_instead_of_dying()
    {
        var reconcile = ShellSource.Load(TabHostSource).Method("ReconcileStripOrder");

        var rebuild = reconcile.Calls("RebuildStripFromManager").ToList();
        Assert.True(
            rebuild.Count == 1,
            "ReconcileStripOrder must fall back to RebuildStripFromManager: skew is "
            + "currently impossible, but a terminal's strip must not die.");
        var catchClause = rebuild.Count == 1
            ? rebuild[0].Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault()
            : null;
        Assert.True(
            catchClause is not null,
            "The rebuild fallback must be reached from a catch, not called inline.");
        var filter = catchClause?.Filter?.ToString() ?? "";
        Assert.True(
            filter.Contains("InvalidOperationException", StringComparison.Ordinal)
                && filter.Contains("KeyNotFoundException", StringComparison.Ordinal),
            "The catch must be filtered to the skew family (Diff's throws and the "
            + "item-map lookup's miss); a bare catch swallows unrelated UI failures.");

        var rebuildMethod = ShellSource.Load(TabHostSource).Method("RebuildStripFromManager");
        Assert.True(
            rebuildMethod.Calls("TabStripProjection.HorizontalRows").Count == 1,
            "The rebuild must take its order from the projector's horizontal " +
            "reading, like the reconcile does: chips occupy slots, so the " +
            "flat Rows list would drop every chip from the strip.");
    }

    [Fact]
    public void The_reconcile_never_leaks_a_dropped_selection_as_an_activation()
    {
        var reconcile = ShellSource.Load(TabHostSource).Method("ReconcileStripOrder");

        var firstMutation = reconcile.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));

        // ListView drops the selection when the selected item is removed
        // and does not restore it on re-insert; live, that drop arrives as
        // an activation of whatever TabView picked instead.
        var assigns = reconcile.AssignsTo("_suppressSelectionEvent").ToList();
        var arms = assigns.Where(a => a.Right.IsKind(SyntaxKind.TrueLiteralExpression)).ToList();
        var disarms = assigns.Where(a => a.Right.IsKind(SyntaxKind.FalseLiteralExpression)).ToList();
        Assert.True(
            arms.Count == 1 && arms[0].SpanStart < firstMutation.SpanStart,
            "ReconcileStripOrder must arm _suppressSelectionEvent before its first removal.");
        Assert.True(
            disarms.Count == 1 && disarms[0].SpanStart > firstMutation.SpanStart,
            "ReconcileStripOrder must disarm _suppressSelectionEvent after the loop.");

        var activate = reconcile.Calls("SelectActive").ToList();
        Assert.True(
            activate.Count == 1 && activate[0].SpanStart > firstMutation.SpanStart,
            "ReconcileStripOrder must call SelectActive after the loop to re-assert "
            + "the manager's active tab.");
    }

    // --- The horizontal chip machinery ---

    [Fact]
    public void The_chip_expand_fence_holds_the_selection_event_shut_until_the_strip_settles()
    {
        var handler = ShellSource.Load(TabHostSource).Method("OnSelectionChanged");
        var chipArm = ChipSelectionArm(handler);

        // (1) The handler opens with the suppress guard. Everything the arm
        // does below runs behind a fence that is DOWN on entry, so the
        // fence it arms itself is the only protection the nested raises
        // have.
        var guard = handler.Body!.Statements.First() as IfStatementSyntax;
        Assert.True(
            guard?.Condition is IdentifierNameSyntax id
                && id.Identifier.ValueText == "_suppressSelectionEvent"
                && guard.Statement is ReturnStatementSyntax { Expression: null },
            "OnSelectionChanged must open with `if (_suppressSelectionEvent) return;`: "
            + "the reverse-sync writes depend on it to no-op nested raises.");

        // (2) The command runs fenced: an arm-true write precedes it. The
        // expand retires the selected chip on this same stack (command ->
        // manager -> group INPC -> reconcile -> TabItems.Remove), and
        // TabView answers that removal by re-targeting the selection and
        // raising this event again -- unfenced, the re-entry either
        // cascade-expands a neighbouring chip or activates whatever it
        // picked.
        var command = chipArm.Call("_router.RequestCollapseGroup");
        var arm = chipArm.AssignsTo("_suppressSelectionEvent")
            .Where(a => a.Right.IsKind(SyntaxKind.TrueLiteralExpression)).ToList();
        Assert.True(
            arm.Count == 1 && arm[0].SpanStart < command.SpanStart,
            "The chip arm must arm _suppressSelectionEvent BEFORE RequestCollapseGroup: "
            + "the expand removes the chip the selection is parked on, and the "
            + "re-entry SelectionChanged must no-op.");

        // (3) The command sits inside the try whose finally disarms after
        // it -- an arm without a finally leaks a stuck fence the first time
        // the command throws.
        var fence = command.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
        var disarm = fence?.Finally?.Block?.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(a => a.Left is IdentifierNameSyntax fid
                && fid.Identifier.ValueText == "_suppressSelectionEvent"
                && a.Right.IsKind(SyntaxKind.FalseLiteralExpression));
        Assert.True(
            fence is not null && disarm is not null
                && fence.Block.Span.Contains(command.Span)
                && disarm.SpanStart > command.SpanStart,
            "The fenced command must sit inside the try block, with the disarm in a "
            + "finally that runs after it.");

        // (4) SelectActive follows the disarm and (5) runs unconditionally:
        // it is the half that lands the real active tab once the strip has
        // settled. Guarded or inside the try, the strip could rest on
        // whatever TabView picked while the chip was removed.
        var select = chipArm.Call("SelectActive");
        Assert.True(
            select.SpanStart > disarm.SpanStart,
            "The chip arm must call SelectActive AFTER the disarm, outside the fence.");
        Assert.True(
            !select.Ancestors().OfType<IfStatementSyntax>()
                .Any(a => a.Span.Start > chipArm.Span.Start),
            "The chip arm's SelectActive must be unconditional: it is the settle-up, "
            + "not a branch of the expand.");

        // (6) And the fence lives HERE, not in RemoveGroupChip. A second
        // fence site is the actively unsafe shape: the rebuild calls
        // ReconcileChips from inside the reconcile's catch, while that
        // fence is still armed, so a disarm in RemoveGroupChip would cut
        // the window short and leave TabItems.Clear() and the re-adds
        // unprotected.
        Assert.Empty(
            ShellSource.Load(TabHostSource).Method("RemoveGroupChip")
                .AssignsTo("_suppressSelectionEvent"));
    }

    [Fact]
    public void The_chip_selection_fork_expands_through_the_command_and_never_activates()
    {
        var chipArm = ChipSelectionArm(
            ShellSource.Load(TabHostSource).Method("OnSelectionChanged"));

        // The expand goes out through the same command path every other
        // collapse/expand source uses, and it never reads as an activation:
        // an Activate here would fire on a click the user aimed at a group,
        // and the wrongly-activated tab would stick, because the arm's
        // SelectActive faithfully re-selects the manager's active tab.
        Assert.Single(chipArm.Calls("_router.RequestCollapseGroup"));
        Assert.Empty(chipArm.Calls("_manager.Activate"));
    }

    [Fact]
    public void The_drop_translates_its_slot_through_the_projection_and_refuses_chip_shaped_drops()
    {
        var completed = ShellSource.Load(TabHostSource).Method("OnTabDragCompleted");

        // The gate and the translation are one polarity: only a TAB drop
        // maps its strip slot through the projection (a raw IndexOf is a
        // slot, not a manager index -- chips occupy slots), because a chip
        // has no manager index to move to.
        var gate = completed.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("item.Tag is not TabGroup", StringComparison.Ordinal));
        var mapping = gate.Call("TabStripProjection.VisibleIndexToModelIndex");
        Assert.True(
            gate.DescendantNodes().Contains(mapping),
            "The drop's slot translation must sit inside the chip-drag gate: a chip "
            + "drop has no manager index to map to.");

        // The refuse: the move is gated on the mapped index, so a drop that
        // came to rest AT a chip's slot maps to -1 and refuses outright --
        // the reconcile after the gate restores the strip. A later rung
        // routes chip-shaped drops into joins.
        var move = completed.Call("_manager.Move");
        Assert.True(
            move.Ancestors().OfType<IfStatementSyntax>()
                .Any(a => a.Condition.ToString() == "newIndex >= 0"),
            "The drop's move must be gated on the mapped index (newIndex >= 0): a "
            + "chip-slot rest maps to -1 and must refuse, not clamp.");
    }

    [Fact]
    public void SelectActive_never_hands_the_selection_to_a_chip()
    {
        var selectActive = ShellSource.Load(TabHostSource).Method("SelectActive");

        // Insurance, not a live branch: chips never enter _itemByModel
        // (AddItem is its only writer, keyed by TabModel), so the item
        // fetched here always carries a null Tag and the bail is
        // unreachable while that holds. Pinned anyway because the whole
        // reverse sync depends on the fetched item BEING the active tab's
        // item -- if a chip ever got in, this bail is what keeps every
        // later activation from reading as an expand request.
        var bail = selectActive.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("item.Tag is TabGroup", StringComparison.Ordinal));
        Assert.True(
            bail.Statement is ReturnStatementSyntax { Expression: null },
            "SelectActive's chip tripwire must bail outright, not re-select the chip.");
        var write = selectActive.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .First(a => a.Left.ToString().EndsWith("SelectedItem", StringComparison.Ordinal));
        Assert.True(
            bail.SpanStart < write.SpanStart,
            "The chip tripwire must sit before the selection write.");
    }

    [Fact]
    public void The_seam_bridge_hides_rather_than_places_from_a_group()
    {
        var bridge = ShellSource.Load(TabHostSource).Method("UpdateSelectedTabBridge");

        // Insurance again, same mechanism as SelectActive's bail: the item
        // out of _itemByModel never carries a group on Tag. The polarity is
        // the point -- a chip here must HIDE the cover (0, 0, null), ahead
        // of the placement math, not place it from a group's slot.
        var bail = bridge.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("item.Tag is TabGroup", StringComparison.Ordinal));
        var hide = bail.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText() == "SelectedTabSeamChanged?.Invoke");
        var placement = bridge.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().Contains("TransformToVisual", StringComparison.Ordinal));
        Assert.True(
            hide.Arg(0) == "0" && hide.Arg(1) == "0" && hide.Arg(2) == "null"
                && bail.SpanStart < placement.SpanStart,
            "A chip at the bridge must hide the cover with (0, 0, null), ahead of "
            + "the placement math.");
    }

    [Fact]
    public void A_close_request_for_a_chip_declines()
    {
        var close = ShellSource.Load(TabHostSource).Method("OnTabCloseRequested");

        // Insurance a third time: chips are IsClosable=false, so no close
        // request can name one while that holds. If close chrome ever
        // leaked onto a chip, this bail is the difference between a
        // declined click and a group closed member by member.
        var bail = close.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("item.Tag is TabGroup", StringComparison.Ordinal));
        Assert.True(
            bail.Statement is ReturnStatementSyntax { Expression: null },
            "A chip named by a close request must decline outright.");
        var requestClose = close.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("RequestCloseTabAsync", StringComparison.Ordinal));
        Assert.True(
            bail.SpanStart < requestClose.SpanStart,
            "The chip decline must precede the close request.");
    }

    [Fact]
    public void The_rebuild_derives_chip_presence_before_it_reads_the_projection()
    {
        var rebuild = ShellSource.Load(TabHostSource).Method("RebuildStripFromManager");

        var presence = rebuild.Call("ReconcileChips");
        var rows = rebuild.Call("TabStripProjection.HorizontalRows");
        Assert.True(
            presence.SpanStart < rows.SpanStart,
            "The rebuild must run ReconcileChips BEFORE walking HorizontalRows: the "
            + "walk reads chips this host must hold, and a rebuild that derived "
            + "presence after it would add rows the walk never saw. Rebuild is what "
            + "the reconcile's refusal falls back to, so this order is load-bearing "
            + "on the skew path, not just the happy one.");
    }

    [Fact]
    public void Every_manager_event_re_derives_chip_presence()
    {
        var ctor = ShellSource.Load(TabHostSource).Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");

        // Chip presence is a projection function all four manager events can
        // move -- a restore arrives grouped, a close retires a run's last
        // member, a move joins a chip'd run, activation decides which run
        // shows member versus chip -- and the events raise whether or not
        // this host is the visible layout. This is the standing half of the
        // belt and braces; RefreshSeam below is the re-derive half.
        foreach (var eventName in new[] { "TabAdded", "TabRemoved", "TabMoved", "ActiveTabChanged" })
        {
            var subscription = ctor.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                            && a.Left.ToString().EndsWith(eventName, StringComparison.Ordinal))
                .ToList();
            Assert.True(
                subscription.Count == 1
                    && subscription[0].Right.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Count(i => i.CalleeText() == "ReconcileChips") == 1,
                $"The manager's {eventName} handler must call ReconcileChips exactly "
                    + "once: chip presence drifts on every one of the four events.");
        }
    }

    [Fact]
    public void The_group_INPC_ride_reconciles_on_collapse_and_refreshes_in_place()
    {
        var handler = ShellSource.Load(TabHostSource).Method("OnGroupPropertyChanged");

        // Collapse raises NO manager event -- group INPC only -- so this
        // handler is the one non-manager ride site for chip presence, and
        // the manager-event census above is structurally blind to it. The
        // IsCollapsed bit changes what the strip HOLDS (a chip mints on
        // fold, retires on unfold), so it takes the full presence-then-
        // order pass before returning; Title and Color are in-place
        // refreshes and fall through to the single-door refresh alone.
        var collapsedBranch = handler.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("IsCollapsed", StringComparison.Ordinal));
        var presence = collapsedBranch.Call("ReconcileChips");
        var order = collapsedBranch.Call("ReconcileStripOrder");
        Assert.True(
            presence.SpanStart < order.SpanStart,
            "A collapse must run the presence pass BEFORE the order pass: the chip "
            + "the fold mints (or retires) has to exist before the order pass reads it.");

        var fallThrough = handler.Call("RefreshChip");
        Assert.True(
            collapsedBranch.Span.End < fallThrough.SpanStart,
            "Title and Color changes must fall through to RefreshChip alone: re-running "
            + "the presence and order passes for an in-place refresh churns the strip "
            + "for nothing.");

        // The ride site is only as live as its wiring: the subscription is
        // minted with the chip and severed with it. Folded into this fact
        // rather than the manager-event census because this is group INPC,
        // not a manager event -- the handler and its wire are one story.
        var src = ShellSource.Load(TabHostSource);
        Assert.Single(src.Method("AddGroupChip").DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                        && a.Left.ToString().EndsWith("PropertyChanged", StringComparison.Ordinal)));
        Assert.Single(src.Method("RemoveGroupChip").DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.MinusEqualsToken)
                        && a.Left.ToString().EndsWith("PropertyChanged", StringComparison.Ordinal)));
    }

    [Fact]
    public void RefreshSeam_re_derives_presence_order_and_selection_and_stays_unfenced()
    {
        var seam = ShellSource.Load(TabHostSource).Method("RefreshSeam");

        var presence = seam.Call("ReconcileChips");
        var order = seam.Call("ReconcileStripOrder");
        var selection = seam.Call("SelectActive");
        Assert.True(
            presence.SpanStart < order.SpanStart && order.SpanStart < selection.SpanStart,
            "The switch-on pass must re-derive presence, then order, then selection: "
            + "the belt for drift a missed subscription cannot confess to.");

        // And it stays UNFENCED. The residual's truthfulness is not the
        // comment but the fork's pin above: the only code that can hold a
        // chip as SelectedItem hands the selection back before returning,
        // so no switch-on removal can leak a selection. A fence added here
        // would look like the fix and paper over that pin instead.
        Assert.Empty(seam.AssignsTo("_suppressSelectionEvent"));
    }

    [Fact]
    public void An_equal_count_content_skew_throws_into_the_rebuild()
    {
        var reconcile = ShellSource.Load(TabHostSource).Method("ReconcileStripOrder");

        // Counts agreeing is not presence agreeing: a stray element no
        // desired row names removes nothing, so the Remove is guarded and
        // the refusal funnels into the same rebuild the count mismatch
        // uses -- the one skew shape the miss flag and the count check
        // slip past.
        var guardedRemove = reconcile.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single(i => i.Condition is PrefixUnaryExpressionSyntax not
                && not.IsKind(SyntaxKind.LogicalNotExpression)
                && not.Operand is InvocationExpressionSyntax call
                && call.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));
        var refusal = guardedRemove.Statement as ThrowStatementSyntax;
        Assert.True(
            refusal is not null,
            "The failed Remove must refuse outright -- a silent fall-through strands "
                + "the stray element past the pass with the repair flag claiming "
                + "health.");
        Assert.True(
            refusal!.Expression is ObjectCreationExpressionSyntax created
                && created.Type.ToString().Contains("InvalidOperationException", StringComparison.Ordinal),
            "The Remove refusal must throw InvalidOperationException -- the filtered "
                + "catch only funnels the skew family.");
        Assert.True(
            refusal.Ancestors().OfType<TryStatementSyntax>().Any(t =>
                t.Catches.Any(c =>
                    c.Filter?.ToString().Contains("InvalidOperationException", StringComparison.Ordinal) == true)),
            "The Remove refusal must sit inside the fenced try, so the filtered catch "
                + "-- not the caller -- sees the skew.");
    }

    // --- Vertical strip reads its order from the projector ---

    [Fact]
    public void Vertical_rebuild_takes_its_order_from_the_projector()
    {
        var rebuild = ShellSource.Load(VerticalSource).Method("RebuildAllItems");

        // Group-aware since the headers rung: the flat Rows list cannot
        // name a header, and a rebuild that walked it would drop every
        // group's header row from the strip.
        Assert.Single(rebuild.Calls("TabStripProjection.GroupedRows"));
        Assert.Empty(rebuild.Calls("TabStripProjection.Rows"));
    }

    // --- Unwired events: no detach path, no re-entrant validation ---

    [Fact]
    public void TabDroppedOutside_stays_unwired()
    {
        // Detach-by-drag is non-goal N1: a drop outside the strip cancels
        // and nothing consumes the event, so any handler is the bypass
        // path. Refused in both places one could appear -- code-behind,
        // which Subscribers sweeps, and the XAML attribute, which compiles
        // into a .g.cs that sweep cannot see (pinned below).
        Assert.Empty(Subscribers("TabDroppedOutside"));

        Assert.False(
            ReadEmbedded(TabHostXaml).Contains("TabDroppedOutside", StringComparison.Ordinal),
            "TabHost.xaml wires TabDroppedOutside; the drag bridge must stay a "
            + "reorder bridge, not grow a detach path.");
    }

    [Fact]
    public void TabItemsChanged_stays_unwired_and_the_reason_lives_at_the_wiring_site()
    {
        // It fires for the hosts' own writes as much as for TabView's, so
        // a validation handler on it re-enters the reconcile that mutates
        // TabItems. If one ever lands it needs a re-entrancy fence and a
        // reason the existing validation points do not cover.
        Assert.Empty(Subscribers("TabItemsChanged"));

        var ctor = ShellSource.Load(TabHostSource).Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");
        Assert.True(
            ctor.ToString().Contains("TabItemsChanged", StringComparison.Ordinal),
            "The decision to leave TabItemsChanged unwired must be recorded in the "
            + "constructor it concerns, where the next reader of the event list looks.");

        // The sweep above reads code-behind only; a XAML attribute compiles
        // into a .g.cs it can never see, so the markup is pinned directly.
        Assert.False(
            ReadEmbedded(TabHostXaml).Contains("TabItemsChanged", StringComparison.Ordinal),
            "TabHost.xaml wires TabItemsChanged; the census above cannot see a XAML attribute.");
    }

    // Every shell file subscribing the event via +=, by resource name.
    private static List<string> Subscribers(string eventName)
    {
        var found = new List<string>();
        foreach (var (resource, root) in ShellSource.AllShellSources())
            if (root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Any(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                              && a.Left.ToString().EndsWith(eventName, StringComparison.Ordinal)))
                found.Add(resource);
        return found;
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    // An equality check between the item's current slot and the target.
    // The target is the projection-mapped slot, not the event's raw `to`:
    // chips occupy slots, so a tab index is not a slot index (chips rung).
    private static bool IsAlreadyInPlaceGuard(IfStatementSyntax ifStatement)
        => ifStatement.Condition is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.EqualsExpression)
            && binary.ToString().Contains("current", StringComparison.Ordinal)
            && binary.ToString().Contains("slot", StringComparison.Ordinal);

    // The `if (item.Tag is TabGroup group)` arm of OnSelectionChanged: the
    // chip click fork every chip-fact below anchors on.
    private static IfStatementSyntax ChipSelectionArm(MethodDeclarationSyntax handler)
        => handler.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains(
                "item.Tag is TabGroup", StringComparison.Ordinal));

    private static bool SetsFlag(MethodDeclarationSyntax method, string field, SyntaxKind literal)
        => method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax id
                      && id.Identifier.ValueText == field
                      && a.Right.IsKind(literal));
}
