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
        var starting = src.Method("BeginHorizontalDragVisual");
        var completed = src.Method("FinishHorizontalDrag");

        Assert.True(
            SetsFlag(starting, "_stripDragActive", SyntaxKind.TrueLiteralExpression),
            "BeginHorizontalDragVisual must raise _stripDragActive (audit 3.2, item 1).");
        Assert.True(
            SetsFlag(completed, "_stripDragActive", SyntaxKind.FalseLiteralExpression),
            "FinishHorizontalDrag must lower _stripDragActive so the cover is placed again.");

        // Synchronous, not queued: a dispatcher hop paints one frame
        // against the stale slot. Nothing else runs between the flag
        // going up and the drag, so deleting the hide must fail here.
        var hide = starting.Calls("SelectedTabSeamChanged?.Invoke").ToList();
        Assert.True(
            hide.Count == 1
                && hide[0].Arg(0) == "0" && hide[0].Arg(1) == "0" && hide[0].Arg(2) == "null",
            "BeginHorizontalDragVisual must hide the cover synchronously with (0, 0, null).");

        // Wired in the constructor: the pointer hooks are where the
        // stale-slot window opens.
        var ctor = src.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");
        var hooked = ctor.DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression.ToString().StartsWith("HookStripDragInput()", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            hooked.Count == 1,
            "The constructor must call HookStripDragInput: the engine's pointer " +
            "hooks are the drag lifecycle's front door.");
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
        var finish = ShellSource.Load(TabHostSource).Method("FinishHorizontalDrag");

        var reconcile = finish.Calls("ReconcileStripOrder").ToList();
        Assert.True(
            reconcile.Count == 1,
            "FinishHorizontalDrag must reconcile the strip to the manager's order.");
        Assert.Empty(reconcile[0].Ancestors().OfType<IfStatementSyntax>());

        // A crossing the manager refused or clamped raises no TabMoved,
        // so nothing else would run the reconcile: the refusal leaves
        // the machine's slot belief standing unless the sweep is
        // unconditional.
    }

    // --- The drop batch: the selection churn a reorder produces ---

    [Fact]
    public void A_live_drag_stands_the_selection_handler_down()
    {
        var handler = ShellSource.Load(TabHostSource).Method("OnSelectionChanged");

        // TabView commits a reorder as a remove-then-insert on TabItems,
        // and the selection model's reaction to the remove re-targets the
        // selection while the dragged tab is still absent: the raise
        // carries a strip the projection cannot describe. Acting on it
        // reconciles inside TabView's still-open modification, and the
        // reconcile's refusal path rebuilds by Clear()ing TabItems there
        // -- the collection refuses a nested modification with
        // 0x8000FFFF, and on the UI thread that is process death.
        var opener = handler.Body!.Statements.First() as IfStatementSyntax;
        Assert.True(
            opener?.Condition is IdentifierNameSyntax id
                && id.Identifier.ValueText == "_suppressSelectionEvent"
                && opener.Statement is ReturnStatementSyntax { Expression: null },
            "OnSelectionChanged must keep the suppress guard as its first statement: " +
            "the reverse-sync writes depend on it before anything else runs.");

        var standDown = handler.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is IdentifierNameSyntax flag
                        && flag.Identifier.ValueText == "_stripDragActive"
                        && StandDownIsInert(i))
            .ToList();
        Assert.True(
            standDown.Count == 1,
            "OnSelectionChanged needs exactly one `if (_stripDragActive)` " +
            "stand-down, and it must stay inert: a bare return, or at most " +
            "the trace's count-and-line before the return. Every selection " +
            "raise a live drag produces is TabView's mid-batch churn, not " +
            "an intent.");
        Assert.True(
            standDown.Count == 1 && standDown[0].SpanStart > opener!.Span.End,
            "The stand-down must follow the suppress guard, which must stay first.");

        // Before the chip fork, because a mid-batch retarget that lands on
        // a chip must not read as the expand gesture, and before the
        // activation walk, because the churn raise is exactly what must
        // never reach the manager as an activation.
        var chipFork = ChipSelectionArm(handler);
        var activate = handler.Calls("_manager.Activate").ToList();
        Assert.True(
            standDown.Count == 1
                && standDown[0].SpanStart < chipFork.SpanStart
                && activate.Count == 1
                && chipFork.SpanStart < activate[0].SpanStart,
            "The stand-down must sit before the chip fork and the activation walk: " +
            "the churn raise would otherwise expand a group or switch the active tab.");
    }

    [Fact]
    public void The_drop_lands_the_selection_once_after_the_batch_has_closed()
    {
        var finish = ShellSource.Load(TabHostSource).Method("FinishHorizontalDrag");

        // The stand-down swallows every selection raise the drag produced,
        // so this landing is the only thing that can end a drop with the
        // strip and the manager agreeing: after the last commit and the
        // reconcile, exactly once.
        var reconcile = finish.Calls("ReconcileStripOrder").ToList();
        var landing = finish.Calls("SelectActive").ToList();
        Assert.True(
            landing.Count == 1 && reconcile.Count == 1
                && landing[0].SpanStart > reconcile[0].Span.End,
            "FinishHorizontalDrag must land the selection exactly once, after the " +
            "commit's reconcile: the order has to be settled before the selection " +
            "is re-asserted against it.");

        // Unconditional. A refused commit and a repaired one must end the
        // same way -- the strip resting on the manager's active tab is the
        // only end state a reorder may leave.
        Assert.Empty(landing[0].Ancestors().OfType<IfStatementSyntax>());
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
        // Only the pass's OWN fence counts: the catch's deferred rebuild
        // carries a lambda-nested saved/restored fence of its own (the
        // deferred attempt runs outside this method's window and owns
        // its fence), and that pair is not this pass's arm and disarm.
        var direct = assigns
            .Where(a => !a.Ancestors().OfType<ParenthesizedLambdaExpressionSyntax>().Any())
            .ToList();
        var arms = direct.Where(a => a.Right.IsKind(SyntaxKind.TrueLiteralExpression)).ToList();
        var disarms = direct.Where(a => a.Right.IsKind(SyntaxKind.FalseLiteralExpression)).ToList();
        Assert.True(
            arms.Count == 1 && arms[0].SpanStart < firstMutation.SpanStart,
            "ReconcileStripOrder must arm _suppressSelectionEvent before its first removal.");
        Assert.True(
            disarms.Count == 1 && disarms[0].SpanStart > firstMutation.SpanStart,
            "ReconcileStripOrder must disarm _suppressSelectionEvent after the loop.");

        var activate = reconcile.Calls("SelectActive")
            .Where(c => !c.Ancestors().OfType<ParenthesizedLambdaExpressionSyntax>().Any())
            .ToList();
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
    public void The_chip_swap_hides_and_restores_the_run_under_a_saved_fence()
    {
        var src = ShellSource.Load(TabHostSource);
        var chips = src.Method("ReconcileChips");

        // The swap owns BOTH halves of presence. A mint replaces the run's
        // rendered members -- whose items must LEAVE TabItems -- and a
        // retirement brings them back. Missing the hide half is the
        // collapse-activate crash: the strip holds one more row than the
        // projection names, the order pass refuses, and the rebuild's
        // Clear runs re-entrantly from inside the raising
        // SelectionChanged -- 0x8000FFFF, the family that killed three
        // owner sessions.
        var retire = chips.Calls("RemoveGroupChip").Single();
        var restore = chips.Call("RestoreRunMembers");
        var mint = chips.Calls("AddGroupChip").Single();
        var hide = chips.Call("HideRunMembers");
        Assert.True(
            retire.SpanStart < restore.SpanStart
                && mint.SpanStart < hide.SpanStart
                && hide.SpanStart > mint.Span.End,
            "Each swap half must carry its presence work: the retirement "
            + "restores the run's members, the mint hides them -- a mint "
            + "without the hide is the presence skew that forces the "
            + "rebuild.");

        // The swap runs under the selection fence, saved and restored --
        // not disarmed. This pass runs inside RebuildStripFromManager's
        // own fence window on the rebuild path, and a naive disarm would
        // cut the outer window short, the RemoveGroupChip fence lesson
        // at the pass that owns the swap.
        var writes = chips.AssignsTo("_suppressSelectionEvent").ToList();
        Assert.True(
            writes.Count == 2
                && writes[0].Right.ToString() == "true"
                && writes[1].Right.ToString() == "outerSuppress"
                && writes[1].Ancestors().OfType<FinallyClauseSyntax>().Any()
                && writes[0].SpanStart < hide.SpanStart
                && writes[1].SpanStart > hide.Span.End,
            "The fence must arm before the swap and restore from the saved "
            + "outer state in the finally: a naive disarm would cut "
            + "RebuildStripFromManager's window short mid-rebuild.");

        // And the halves themselves: the hide removes exactly the run's
        // members, the restore re-adds exactly what is missing.
        var hideBody = src.Method("HideRunMembers");
        Assert.True(
            hideBody.Calls("TabViewControl.TabItems.Remove").Count == 1
                && hideBody.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(c => c.CalleeText() == "_itemByModel.TryGetValue"),
            "The hide removes the run's member items and nothing else.");
        var restoreBody = src.Method("RestoreRunMembers");
        var add = restoreBody.Call("TabViewControl.TabItems.Add");
        Assert.True(
            add.Ancestors().OfType<IfStatementSyntax>()
                .Any(i => i.Condition.ToString().Contains(
                    "!TabViewControl.TabItems.Contains(item)", StringComparison.Ordinal)),
            "The restore re-adds a member only when the strip does not hold "
            + "it: re-adding a rendered row duplicates it.");

        // And the fade is real, not prose: the Add is followed by the
        // swap flag's guarded FadeInAppearing -- the appear-hand the
        // retirement armed, spent on exactly these rows. Deleted, the
        // members snap in past the appear-hand and nothing else in the
        // suite would know.
        var guardedFade = restoreBody.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_swapFadePending");
        Assert.True(
            guardedFade.SpanStart > add.Span.End
                && guardedFade.Statement.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(c => c.CalleeText() == "FadeInAppearing"),
            "The restore's Add must be followed by the swap flag's guarded "
            + "FadeInAppearing: the retirement armed the flag for exactly "
            + "these rows, and a restore without the fade snaps the run in "
            + "past the swap's appear-hand.");
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
    public void A_chip_press_arms_the_unit_space_machine()
    {
        var pressed = ShellSource.Load(TabHostSource).Method("OnStripPointerPressed");
        var armChip = ShellSource.Load(TabHostSource).Method("ArmChipDrag");

        // A chip drags its whole run, and the arm routes the press to the
        // unit-space machine: one slot per body run, the run the atom, so
        // a crossing can offer a landing inside a neighbouring run that
        // the projector could not render.
        // The arm routes on the tag, in one expression: a chip's press
        // goes to the unit-space arm, anything else to the plain one.
        var route = pressed.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Single(c => c.Condition.ToString() == "item.Tag is TabGroup group");
        var arm = Assert.IsType<InvocationExpressionSyntax>(route.WhenTrue);
        Assert.Equal("ArmChipDrag", arm.CalleeText());
        Assert.Equal("(group, item, pressX)", arm.ArgumentList.ToString());
        Assert.Equal("ArmTabDrag",
            Assert.IsType<InvocationExpressionSyntax>(route.WhenFalse).CalleeText());

        // The unit machine is TabGroupDragUnits' build, and the dragged
        // unit is found by GROUP IDENTITY -- index arithmetic here is how
        // a wrong-run drag hides.
        Assert.Single(armChip.Calls("TabGroupDragUnits.Build"));
        var identity = armChip.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "ReferenceEquals");
        Assert.True(
            identity.ArgumentList.ToString() == "(units[i].Group, group)",
            "The dragged unit must be found by group identity: positioning the "
            + "machine on any other run drags a run the user never grabbed.");

        // The pinned prefix contributes no units and MoveGroup clamps as
        // the backstop, but the arm itself still refuses a strip it
        // cannot measure: BuildSession answers null, the press stays a
        // click.
        Assert.Single(armChip.Calls("BuildSession"));

        // And WHICH element a unit's center comes from: the minted chip
        // first -- a chip'd collapse renders no member at all, so the
        // chip is the run's visible atom and the honest center -- else
        // the first member the strip ACTUALLY renders. Collapse does not
        // prune the item map, so first-in-map can answer the run's
        // detached head, whose geometry is a refusal: that is exactly
        // the trap that dead-drove every chip drag once, and its old
        // bytes must stay red here.
        var rep = ShellSource.Load(TabHostSource).Method("UnitRepresentative");
        var chipTry = rep.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "_chipByGroup.TryGetValue");
        var chipGate = chipTry.Ancestors().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().StartsWith(
                "_chipByGroup.TryGetValue", StringComparison.Ordinal));
        var memberLoop = rep.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Single(f => f.Expression.ToString() == "_manager.MembersOf(group)");
        var rendered = memberLoop.Statement.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains(
                "TabViewControl.TabItems.IndexOf(item) >= 0", StringComparison.Ordinal));
        Assert.True(
            chipGate.SpanStart < memberLoop.SpanStart,
            "The chip must speak before the member walk: under a chip'd "
            + "collapse the first map entry is a detached head, and measuring "
            + "it refuses every chip drag at the arm.");
        Assert.Contains("TabViewControl.TabItems.IndexOf(item) >= 0",
            rendered.Condition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_threshold_release_is_a_click_the_engine_leaves_alone()
    {
        var released = ShellSource.Load(TabHostSource).Method("OnStripPointerReleased");

        // The begin bundle never ran for a press still under the
        // threshold: the flag is down, the seam is placed, the lift does
        // not exist. The release must answer that press with nothing but
        // the machine's own cancel -- finishing it as a drag would run
        // the landing, reconcile against a pointer the strip never
        // showed, and steal the click the item's own pipeline is
        // answering on the same event.
        var gate = Assert.IsType<IfStatementSyntax>(
            released.Body!.Statements.ElementAt(2));
        Assert.Equal("drag.Machine.Phase != TabDragPhase.Dragging",
            gate.Condition.ToString());
        var arm = Assert.IsType<BlockSyntax>(gate.Statement);
        Assert.Equal(2, arm.Statements.Count);
        Assert.Contains(arm.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "drag.Machine.Cancel");
        Assert.True(
            arm.Statements.Last() is ReturnStatementSyntax { Expression: null },
            "the sub-threshold arm must return: the finish pass is for drags "
            + "that began, never for clicks.");

        // And the disarm stands before everything else: the guard is the
        // handler's first statement, and the field is down at the second,
        // so a re-entrant release finds no session to finish a second
        // time.
        var statements = released.Body!.Statements.ToList();
        var guard = Assert.IsType<IfStatementSyntax>(statements[0]);
        Assert.Equal("_horizontalDrag is not { } drag", guard.Condition.ToString());
        Assert.True(
            guard.Statement is ReturnStatementSyntax { Expression: null },
            "a release with no session of ours belongs to whoever owns it.");
        Assert.True(
            statements[1] is ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax
                {
                    Left: IdentifierNameSyntax { Identifier.ValueText: "_horizontalDrag" },
                    Right: LiteralExpressionSyntax { Token.ValueText: "null" }
                }
            },
            "the handler must disarm before any pass runs: a re-entrant release "
            + "must not finish the same drag twice.");
    }

    [Fact]
    public void A_crossing_dispatches_and_refusals_stop_the_tick()
    {
        var commit = ShellSource.Load(TabHostSource).Method("CommitHorizontalCrossing");
        var moved = ShellSource.Load(TabHostSource).Method("OnStripPointerMoved");

        // The dispatcher is identity-only: a session carries exactly one
        // of tab or group, and each kind owns its own commit. Both arms
        // answer a bool, and the moved loop stops on false -- a refused
        // crossing rewound the machine, so the same center would re-earn
        // the same refusal forever if the tick kept asking.
        Assert.Contains("CommitTabCrossing(drag, crossing)",
            commit.ExpressionBody?.Expression.ToString(), StringComparison.Ordinal);
        Assert.Contains("CommitGroupCrossing(drag, group, crossing)",
            commit.ExpressionBody?.Expression.ToString(), StringComparison.Ordinal);
        var loop = moved.DescendantNodes().OfType<WhileStatementSyntax>().Single();
        Assert.Contains("if (!CommitHorizontalCrossing(drag, crossing)) break;",
            loop.Statement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tab_crossing_classifies_the_pin_boundary_before_the_move()
    {
        var commit = ShellSource.Load(TabHostSource).Method("CommitTabCrossing");

        // The boundary is classified FIRST, against the projection's
        // manager target: a crossing over the pinned prefix is a zone
        // change Move alone would clamp away, so SetPinned relocates the
        // row to the boundary and the Move then places it at the
        // crossing's slot in the new zone.
        var classify = commit.Call("TabPinBoundary.Classify");
        Assert.True(
            classify.Arg(0) == "drag.Tab!.IsPinned"
                && classify.Arg(1) == "_manager.PinCount"
                && classify.Arg(2) == "_manager.Tabs.Count"
                && classify.Arg(3) == "managerTo",
            "The boundary must be classified from the machine's manager target: "
            + "classifying from the strip slot, or after the move, pins the "
            + "wrong row or pins nothing.");
        var setPinned = commit.Call("_manager.SetPinned");
        var move = commit.Call("_manager.Move");
        Assert.True(
            setPinned.SpanStart < move.SpanStart,
            "SetPinned must precede the Move: the pin relocates the row to the "
            + "boundary, and the move's `from` is read fresh from it.");

        // The truth is read back after the commit, and the two failures
        // have two ends. A clamp -- Move no-ops at the boundary -- rewinds
        // the machine to the slot the strip actually shows and refuses
        // the tick. A vanished row -- the pairing no longer names it, a
        // chord-collapse of its run, say -- CANCELS: refusing without
        // rewinding or cancelling leaves a zombie session committing for
        // a row the strip does not show.
        var vanish = commit.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "actualSlot < 0");
        Assert.True(
            vanish.Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText() == "CancelHorizontalDrag")
                && vanish.Statement.DescendantNodes().OfType<ReturnStatementSyntax>().Any(),
            "A vanished row must cancel the drag: a refuse-without-rewind arm "
            + "zombies the session on, committing for a row the strip does "
            + "not show.");
        var clamp = commit.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "actual != managerTo");
        Assert.True(
            clamp.Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText() == "drag.Machine.UpdateIndex"),
            "A crossing that did not land must rewind the machine to the actual "
            + "slot and refuse: the engine owns the visual now, and a silent "
            + "clamp would strand the row on a slot it never reached.");

        // The read-back comes after the Move: it reads the post-commit
        // pairing, the pre-commit one being stale past the displaced rows.
        Assert.True(clamp.SpanStart > move.Span.End,
            "The read-back must follow the move; before it, the pairing is the "
            + "pre-commit state and the read lies.");
    }

    [Fact]
    public void A_chip_crossing_maps_through_the_unit_formulas_and_reads_the_truth_back()
    {
        var commit = ShellSource.Load(TabHostSource).Method("CommitGroupCrossing");

        // The crossing maps through the unit formulas and nothing else:
        // down swaps past the pivot's whole span (After, with the dragged
        // run's own departure subtracted), up lands before the pivot's
        // head (Before). Slot arithmetic here is the transposition trap
        // 5a flagged -- a strip-slot read past a chip names a member, not
        // a run, and would split the run the projector draws as one.
        var down = commit.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "TabGroupDragUnits.TargetAfter");
        Assert.True(
            down.ArgumentList.ToString() == "(units, units[dragged], pivot)",
            "The downward target must come from TargetAfter with the dragged "
            + "unit and the pivot: any other index math splits a run the "
            + "projector renders as one.");
        var up = commit.Call("TabGroupDragUnits.TargetBefore");
        Assert.Equal("(units, pivot)", up.ArgumentList.ToString());
        var direction = commit.Body!.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "down");
        Assert.Equal("pivot > dragged", direction.Initializer!.Value.ToString());
        var chosen = commit.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Single(c => c.Condition.ToString() == "down");
        Assert.True(
            chosen.WhenTrue.ToString().EndsWith("TargetAfter(units, units[dragged], pivot)",
                StringComparison.Ordinal)
            && chosen.WhenFalse.ToString().EndsWith("TargetBefore(units, pivot)",
                StringComparison.Ordinal),
            "Direction decides the formula: After for a downward crossing, "
            + "Before for an upward one -- swapped, every drag lands one run "
            + "short.");

        // MoveGroup clamps, so the truth is read back against a fresh
        // unit build: a crossing that did not land rewinds the machine to
        // the run's actual unit and refuses the tick.
        var groupCommit = commit.Call("_manager.MoveGroup");
        var groupVanish = commit.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "now < 0");
        Assert.True(
            groupVanish.SpanStart > groupCommit.Span.End
                && groupVanish.Statement.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(c => c.CalleeText() == "CancelHorizontalDrag"),
            "A run that left the unit space mid-drag must cancel the drag, the "
            + "same zombie rule the tab path follows.");
        var readBack = commit.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "nowUnits[now].First != target");
        Assert.True(
            readBack.SpanStart > groupCommit.Span.End
                && readBack.Statement.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(c => c.CalleeText() == "drag.Machine.UpdateIndex"),
            "The read-back must follow MoveGroup and rewind on a clamp: the "
            + "formula's promise is checked against the manager's answer, "
            + "never assumed.");

        // The commit is MoveGroup's, never a member Move: a Move here
        // would relocate one member out of the run the chip is carrying.
        Assert.Empty(commit.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "_manager.Move"));
    }

    [Fact]
    public void A_drop_on_a_chip_joins_through_the_projector_and_only_by_geometry()
    {
        var fork = ShellSource.Load(TabHostSource).Method("ResolveDropAtChip");

        // The run that took the drop comes from the projection: the
        // members were hidden at drop time, so no TabItems index can say
        // which run a chip slot names.
        Assert.Empty(fork.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("TabItems.IndexOf", StringComparison.Ordinal)));
        Assert.Single(fork.Calls("TabStripProjection.VisibleGroupAt"));

        // ON the chip joins, and only ON: the same -1 slot also serves a
        // drop parked beside the chip, and only the recorded pointer
        // geometry tells those apart. An unconditional join would swallow
        // every positioning drop a chip borders.
        var join = fork.Call("_manager.JoinGroup");
        Assert.True(
            join.Ancestors().OfType<IfStatementSyntax>()
                .Any(a => a.Condition.ToString().Contains("Contains(_lastDropPosition)",
                    StringComparison.Ordinal)),
            "The join must be gated on the pointer landing inside the chip's bounds: "
            + "a drop beside the chip positions, it does not join.");

        // Beside the chip positions relative to the run's edge -- both
        // directions present, and through Move, never a membership write.
        Assert.Single(fork.Calls("TabChipDrop.MemberTargetBefore"));
        Assert.Single(fork.Calls("TabChipDrop.MemberTargetAfter"));
        var beside = fork.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText().EndsWith(".Move", StringComparison.Ordinal));
        Assert.True(
            beside.SpanStart > join.SpanStart,
            "The positioning move must be the fork's second arm, after the join.");
    }

    [Fact]
    public void The_engine_hooks_the_pointer_without_capture_and_owns_the_drop_point()
    {
        var src = ShellSource.Load(TabHostSource);
        var hook = src.Method("HookStripDragInput");
        var finish = src.Method("FinishHorizontalDrag");

        // The five pointer handlers are wired on the host's own root with
        // handledEventsToo, the vertical's proven shape -- and nowhere in
        // the hook or the handlers does a capture appear: presses route
        // by hit-testing, which is why a sub-threshold press is still the
        // click it started as.
        Assert.Equal(5, hook.Calls("HostRoot.AddHandler").Count());
        Assert.All(
            hook.Calls("HostRoot.AddHandler"),
            c => Assert.True(
                c.Arg(1).StartsWith("new PointerEventHandler", StringComparison.Ordinal)
                && c.Arg(2) == "true",
                "every pointer hook must carry its handler with handledEventsToo: "
                + "true -- a press over any part of an item arms."));
        // CalleeText keeps the receiver, so the no-capture pin must match
        // by suffix: a bare-name match can never go red, because
        // CapturePointer is only ever spelled with something before the
        // dot.
        Assert.Empty(src.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("CapturePointer", StringComparison.Ordinal)));

        // The drop point is the engine's own release event now, recorded
        // in the finish pass: TabView no longer supplies one (no OLE
        // drag), and the join fork's geometry reads what the pointer
        // actually did.
        var record = finish.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .First(a => a.Left is IdentifierNameSyntax lid
                        && lid.Identifier.ValueText == "_lastDropPosition");
        Assert.True(
            record.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText().EndsWith("GetCurrentPoint", StringComparison.Ordinal)),
            "The recorded point must come from the release event's position.");

        // The switch-off is total: no OLE drag handshake in the markup,
        // and the reorder engine it used to feed is off with it.
        var xaml = ReadEmbedded(TabHostXaml);
        Assert.Contains("CanReorderTabs=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanDragTabs=\"False\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TabStripDragOver", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TabStripDrop", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TabDragStarting", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TabDragCompleted", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chip_carries_the_group_menu_through_the_router()
    {
        var addChip = ShellSource.Load(TabHostSource).Method("AddGroupChip");
        var menu = addChip.Call("TabContextMenuBuilder.BuildGroupMenu");

        // The menu is the chip's ContextFlyout: while the run is folded,
        // the chip is the run's only surface, so the group commands live
        // there and nowhere else.
        Assert.True(
            menu.Ancestors().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "ContextFlyout"),
            "BuildGroupMenu must be attached as the chip's ContextFlyout.");

        // Every item routes through the router, which is where the
        // announcement is guaranteed; a direct manager call here would
        // move group state silently.
        var args = menu.ArgumentList.ToString();
        foreach (var route in new[] { "RequestCollapseGroup", "RequestDissolveGroup",
                     "RequestCloseGroup", "RequestRenameGroup", "RequestColorGroup" })
        {
            Assert.True(
                args.Contains(route, StringComparison.Ordinal),
                $"The chip menu's {route} must route through the router.");
        }
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

        // The two arms are exclusive, expressed as CONTAINMENT rather than as
        // source order. They are an if/else now, so the collapse branch's span
        // ends after the in-place arm's body and an ordering test reads the
        // structure backwards -- it would fail on correct code and pass on a
        // rewrite that put the refresh back inside the collapse.
        var collapseArm = collapsedBranch.Statement;
        var refreshArm = collapsedBranch.Else?.Statement;
        Assert.True(refreshArm is not null,
            "the in-place refresh has no arm of its own, so a collapse and a rename "
            + "cannot be doing different work");

        Assert.Empty(collapseArm.Calls("RefreshChip"));
        Assert.NotEmpty(refreshArm!.Calls("RefreshChip"));
        Assert.True(
            refreshArm.Calls("ReconcileChips").Count == 0
                && refreshArm.Calls("ReconcileStripOrder").Count == 0,
            "Title and Color changes must not re-run the presence and order passes: "
            + "re-running them for an in-place refresh churns the strip for nothing.");

        // Both arms end at the bridge pass, which is the only thing that places
        // and inks the field's cap and end bar. Outside the arms rather than in
        // each, because both need it and neither raises a manager event the
        // pass already rides: the active-visible rule keeps the active tab out
        // of the hidden set, so a collapse raises no ActiveTabChanged, and the
        // control's own size does not move. Without it a collapse left both
        // bars spanning the run they used to cover -- in the middle of
        // unrelated tabs, since equal width re-lays every remaining one -- and
        // a recolour left them the old colour.
        var bridge = Assert.Single(handler.Calls("QueueBridgeUpdate"));
        Assert.True(
            bridge.SpanStart > collapsedBranch.Span.End,
            "the bridge update sits inside one arm, so the other group change "
            + "leaves the field's terminals where and how they were");

        // The ride site is only as live as its wiring, and the wiring's
        // LIFETIME is the whole point.
        //
        // This used to require the subscription to be minted in AddGroupChip
        // and severed in RemoveGroupChip. That pairing was the bug (#871), not
        // the invariant: a chip is only minted once a run is ALREADY
        // collapsed, so an expanded run had no listener, its collapse was
        // never heard, and the strip went on rendering the members until some
        // unrelated manager event or a layout switch's RefreshSeam re-derived
        // presence. Chip presence is a projection of the collapse bit, so the
        // listener has to outlive the chip rather than be minted by it.
        //
        // What is required now is the opposite pairing, and it is strictly
        // stronger: the hook follows the MANAGER'S GROUPS, and the chip
        // methods must not touch it at all.
        var src = ShellSource.Load(TabHostSource);

        foreach (var chipMethod in new[] { "AddGroupChip", "RemoveGroupChip" })
        {
            Assert.Empty(src.Method(chipMethod).DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => (a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                             || a.OperatorToken.IsKind(SyntaxKind.MinusEqualsToken))
                            && a.Left.ToString().EndsWith("PropertyChanged", StringComparison.Ordinal)));
        }

        // Both directions, in the one pass that owns the hook. Subscribe only
        // would leak a handler per dissolved group; unsubscribe only is the
        // deafness this fixed.
        var hooks = src.Method("ReconcileGroupHooks");
        Assert.Single(hooks.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                        && a.Left.ToString().EndsWith("PropertyChanged", StringComparison.Ordinal)
                        && a.Right.ToString() == "OnGroupPropertyChanged"));
        Assert.Single(hooks.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.MinusEqualsToken)
                        && a.Left.ToString().EndsWith("PropertyChanged", StringComparison.Ordinal)
                        && a.Right.ToString() == "OnGroupPropertyChanged"));

        // Driven by the manager's groups, not by anything chip-shaped. This is
        // what makes the hook's lifetime the group's.
        Assert.Contains(
            "_manager.Groups",
            hooks.ToString(),
            StringComparison.Ordinal);

        // And the presence pass has to arm the listener BEFORE it reads the
        // bit, so the pass that acts on a collapse is also the pass that can
        // hear the next one. Ordering, not mere presence: hooking after the
        // presence work is done still reads as a call.
        var chips = src.Method("ReconcileChips");
        var hookCall = chips.Call("ReconcileGroupHooks");
        var firstDesired = chips.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("HorizontalRows", StringComparison.Ordinal));
        Assert.True(
            hookCall.SpanStart < firstDesired.SpanStart,
            "ReconcileChips must arm the group hooks before it reads the projection: the pass that "
            + "acts on a collapse is the pass that has to be able to hear the next one");

        // Teardown, so the manager cannot hold the strip alive through a group
        // that outlives it.
        Assert.Single(src.Method("UnhookAllGroups").DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.MinusEqualsToken)
                        && a.Left.ToString().EndsWith("PropertyChanged", StringComparison.Ordinal)
                        && a.Right.ToString() == "OnGroupPropertyChanged"));
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

    // The stand-down may count and trace, and nothing else. A bare return
    // is the original inert shape; the block form is inert exactly when it
    // holds only the counter bump, the trace line, and the return -- any
    // other statement in there would be the drag acting on its own churn.
    private static bool StandDownIsInert(IfStatementSyntax standDown) => standDown.Statement switch
    {
        ReturnStatementSyntax { Expression: null } => true,
        BlockSyntax block => block.Statements.Count == 3
            && block.Statements[0] is ExpressionStatementSyntax bump
            && bump.Expression is PostfixUnaryExpressionSyntax
            {
                OperatorToken.ValueText: "++"
            } counted
            && counted.Operand is IdentifierNameSyntax
            {
                Identifier.ValueText: "_stoodDownSelectionRaises"
            }
            && block.Statements[1] is ExpressionStatementSyntax trace
            && trace.Expression.AssertCallTo("TabDragTrace.Line") is not null
            && block.Statements[2] is ReturnStatementSyntax { Expression: null },
        _ => false,
    };

    private static bool SetsFlag(MethodDeclarationSyntax method, string field, SyntaxKind literal)
        => method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax id
                      && id.Identifier.ValueText == field
                      && a.Right.IsKind(literal));

    [Fact]
    public void The_body_session_carries_its_tab_and_the_fork_resolves_the_chips_slot()
    {
        var src = ShellSource.Load(TabHostSource);

        // The body session must CARRY the dragged tab: a null tab survives
        // the arm, then the first crossing dies on
        // _manager.IndexOf(null) -- a NullReferenceException mid-gesture,
        // mid-strip, with the pointer still down.
        var armTab = src.Method("ArmTabDrag");
        var build = armTab.Call("BuildSession");
        Assert.Equal("dragged", build.Arg(0));

        // And a release that lands on a chip resolves THAT chip's slot
        // for the join fork: the dragged tab's own slot is an item slot by
        // definition, so feeding it to the projector refuses every join
        // before geometry is ever asked.
        var finish = src.Method("FinishHorizontalDrag");
        var chipSlot = finish.Body!.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "chipSlot");
        var fork = finish.Calls("ResolveDropAtChip").Single();
        Assert.True(
            chipSlot.SpanStart < fork.SpanStart
                && fork.Arg(1) == "chipSlot",
            "The join fork must resolve the released-on chip's slot: the "
            + "dragged tab's own slot never names a group, so the fork "
            + "refused every join before geometry was asked.");
    }

    /// <summary>
    /// The reconcile's rebuild is the last resort, and its worst failure
    /// mode was landing inside MUXC's still-open container state (the
    /// collapse-activate crash). The catch must therefore hand the
    /// rebuild to the retry executor -- which yields off the foreign
    /// frame and re-queues -- with the attempt carrying its own
    /// saved/restored selection fence (fence-down owns its fence).
    /// </summary>
    [Fact]
    public void The_rebuild_defers_off_the_foreign_frame_through_the_retry()
    {
        var reconcile = ShellSource.Load(TabHostSource).Method("ReconcileStripOrder");
        var retry = reconcile.Call("ReconcileRetry.Rebuild");

        // The retry carries the rebuild and the landing: the attempt is
        // fenced saved/restored (a deferred attempt runs OUTSIDE this
        // method's fence window and owns its own), and the landing
        // re-asserts selection, because the tail's gate has already
        // run by the time a deferred attempt lands.
        var attempt = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(
            retry.ArgExpression(1));
        var attemptText = attempt.ToFullString();
        Assert.Contains("_suppressSelectionEvent = true", attemptText, StringComparison.Ordinal);
        Assert.Contains("_suppressSelectionEvent = outerSuppress",
            attemptText, StringComparison.Ordinal);
        var landed = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(retry.ArgExpression(2));
        Assert.Contains("SelectActive()", landed.ToFullString(), StringComparison.Ordinal);
    }
}
