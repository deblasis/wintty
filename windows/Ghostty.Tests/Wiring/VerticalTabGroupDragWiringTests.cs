using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The run drag's wiring (5b-3b): a header press arms the same PR 3
/// machine over the unit space 3a landed, a crossing commits through
/// MoveGroup with the unit formulas as the only crossing-to-index
/// mapping, visible members ride the header's follow as cargo, and the
/// pointer toggle's stand-down is phase-aware because a header press
/// now arms a session at all. Model half: TabGroupDragUnitsTests (the
/// formulas, executed); the unit space itself is pinned there.
///
/// Wiring guards, not behaviour tests: whether a run lands on the right
/// pixels under a real pointer is only observable on a live strip.
/// </summary>
public class VerticalTabGroupDragWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    /// <summary>
    /// The arm rides the press, before the tab path can claim the item:
    /// a header is not a TabModel row, but waiting for the tab guard to
    /// reject it would leave the arm wedged behind a return. The machine
    /// is the one PR 3 machine -- constructed over UNITS, pressed the
    /// same way -- and the refusals are the drag's own: a run with no
    /// members, a strip with nothing to swap, and an identity grab that
    /// missed. Session state carries the group and the run's head tab,
    /// so every downstream guard (RemoveItem's close check, cancel
    /// rollback) keeps working on the same fields the one-row drag uses.
    /// </summary>
    [Fact]
    public void AHeaderPress_ArmsTheRunDrag_ThroughTheSameMachine()
    {
        var pressed = Strip().Method("OnDragPointerPressed");
        var arm = pressed.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                == "item is VerticalTabGroupHeaderItem { Tag: TabGroup group }");
        Assert.NotEmpty(arm.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>().Where(c => c.CalleeText() == "ArmGroupDrag"));
        Assert.NotEmpty(arm.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>());

        // Before the tab path: the header has no TabModel tag, so the tab
        // guard's return would swallow the press first.
        var tabGuard = pressed.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("ReferenceEquals(owned, item)"));
        Assert.True(arm.Span.Start < tabGuard.Span.Start,
            "the header arm must precede the tab path's ownership guard");

        var armMethod = Strip().Method("ArmGroupDrag");
        Assert.Single(armMethod.Calls("TabGroupDragUnits.Build"));

        // A single-unit strip has nothing to swap (the machine's ctor
        // throws below two rows): the arm refuses before constructing.
        var refusal = armMethod.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "units.Count < 2");
        var session = armMethod.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "DragSession");
        Assert.True(refusal.Span.End < session.Span.Start,
            "the single-unit refusal must precede the session it refuses to arm");
        Assert.NotEmpty(refusal.Statement.DescendantNodesAndSelf()
            .OfType<ReturnStatementSyntax>());

        // The same machine, slots = units, grab = the run's unit index.
        var machine = armMethod.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "TabDragReorder");
        Assert.Equal("units.Count", machine.ArgumentList.Arguments[0].ToString());
        Assert.Equal("grabbed", machine.ArgumentList.Arguments[1].ToString());
        Assert.Single(armMethod.Calls("machine.Press"));

        // The grab is identity, never index math (the 5b-1 lesson one
        // level up), and a missed grab refuses the arm.
        var grab = armMethod.Calls("RunUnitIndex").First();
        Assert.Equal(new[] { "units", "group" }, grab.ArgumentList.Arguments
            .Select(a => a.ToString()).ToArray());
        var missed = armMethod.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "grabbed < 0");
        Assert.NotEmpty(missed.Statement.DescendantNodesAndSelf()
            .OfType<ReturnStatementSyntax>());

        var initializers = session.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
            .ToDictionary(a => a.Left.ToString(), a => a.Right.ToString());
        Assert.Equal("run[0]", initializers["Tab"]);
        Assert.Equal("group", initializers["Group"]);
        Assert.Equal("machine", initializers["Machine"]);
        Assert.Equal("header", initializers["PressRow"]);
    }

    /// <summary>
    /// The run tick is the tab tick's skeleton with a different commit:
    /// MoveGroup, fenced by the commit churn (the churn's Remove+Add is
    /// what would otherwise cancel the drag mid-commit), with the truth
    /// read back afterwards -- a clamp that swallowed the placement must
    /// break, not continue into a re-fired identical crossing. The unit
    /// formulas are the ONLY crossing-to-index mapping, and the ternary's
    /// polarity is the pin for 3a's review nit: TargetAfter and
    /// TargetBefore are one transposition apart, a call site that picks
    /// the wrong direction compiles to a plausible wrong head slot, and
    /// no clamp corrects a target that is merely plausible. No pin work
    /// and no ghost: groups cannot be pinned, so there is nothing to
    /// promise and nothing to honour at release.
    /// </summary>
    [Fact]
    public void TheRunDrag_TicksThroughMoveGroup_WithTheFormulasAsTheOnlyMapping()
    {
        var evaluate = Strip().Method("EvaluateDrag");

        // The fork hands the whole tick over before the one-row path's
        // slot pairing runs: the machine's slots are units here, and
        // DragSlots speaks tab rows.
        var fork = evaluate.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "drag.Group is not null");
        Assert.NotEmpty(fork.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>().Where(c => c.CalleeText() == "EvaluateRunDrag"));
        Assert.NotEmpty(fork.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>());
        // First, not Single: the tab path pairs slots twice (the tick's
        // pairing and the refused-crossing re-derivation), and the fork
        // must precede the first of them.
        Assert.True(fork.Span.End < evaluate.Calls("DragSlots").First().Span.Start,
            "the run fork must precede the tab path's slot pairing");

        var run = Strip().Method("EvaluateRunDrag");

        // MoveGroup is the commit -- and nothing else is: Move (a row at
        // a time) would split the run across the crossing, and the pin
        // grammar has no business in a path that cannot reach the zone.
        Assert.Single(run.Calls("_manager.MoveGroup"));
        Assert.Empty(run.Calls("_manager.Move"));
        Assert.Empty(run.Calls("_manager.SetPinned"));
        Assert.Empty(run.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == "_pinPreview"));

        // And no ghost, in any spelling: the run drag never promises a
        // shelf slot, so the preview pass has no call site here at all.
        Assert.Empty(run.Calls("UpdatePinPreview"));
        Assert.Empty(run.Calls("ShowPinPreview"));
        Assert.Empty(run.Calls("HidePinPreview"));

        // The churn fence wraps the commit: true before, false in the
        // finally, so a mid-commit close cannot cancel the drag out of
        // RemoveItem.
        var move = run.Calls("_manager.MoveGroup").Single();
        var churn = run.AssignsTo("_commitChurn").ToList();
        Assert.Equal(2, churn.Count);
        Assert.Equal("true", churn.Single(a => a.Span.Start < move.Span.Start).Right.ToString());
        Assert.Equal("false", churn.Single(a => a.Span.Start > move.Span.Start).Right.ToString());

        // The mapping, pinned at the call site (3a nit 2): down crosses
        // through TargetAfter fed the DRAGGED unit and the pivot, up
        // through TargetBefore fed the pivot -- swapped arms or swapped
        // arguments are plausible wrong head slots.
        var ternary = run.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Single(c => c.Condition.ToString() == "down");
        var after = ternary.WhenTrue.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "TabGroupDragUnits.TargetAfter");
        Assert.Equal("units[dragged]", after.Arg(1));
        Assert.Equal("pivot", after.Arg(2));
        var before = ternary.WhenFalse.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "TabGroupDragUnits.TargetBefore");
        Assert.Equal("pivot", before.Arg(1));

        // The flag is the direction choice the whole mapping rides, so
        // the declaration's initializer is pinned like the churn fence's
        // assignments: inverted, the strip commits TargetBefore on
        // downward crossings while the ternary above still reads true.
        var down = run.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Single(l => l.Declaration.Variables
                .Any(v => v.Identifier.ValueText == "down"));
        Assert.Equal("crossing.To > crossing.From",
            down.Declaration.Variables.Single().Initializer!.Value.ToString());

        // The read-back: the landed unit's First must equal the target,
        // and a miss breaks the loop -- a continue would re-fire the
        // identical refused crossing forever, because Evaluate is pure
        // per tick.
        var refused = run.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "now < 0 || nowUnits[now].First != target");
        Assert.NotEmpty(refused.Statement.DescendantNodesAndSelf()
            .OfType<BreakStatementSyntax>());
        Assert.Empty(refused.Statement.DescendantNodesAndSelf()
            .OfType<ContinueStatementSyntax>());
    }

    /// <summary>
    /// Collapse stays as the user left it (a drag is not a command to
    /// unfold), and hidden members are never cargo: they have no
    /// arranged geometry, so both the follow's rig and the glides take
    /// their rows through the one visible-member walk -- Edge-135's own
    /// visibility rule, stated once per walk and never re-derived at a
    /// call site that could drift from it.
    /// </summary>
    [Fact]
    public void CollapseStaysAsTheUserLeftIt_AndHiddenMembersAreNeverCargo()
    {
        var strip = Strip();
        foreach (var method in new[]
                 {
                     "ArmGroupDrag", "StartDragVisual", "EvaluateRunDrag",
                     "AttachCoDrag", "DetachCoDrag", "RebindFollow",
                     "StartRunGapGlides", "GlideUnit",
                 })
        {
            Assert.Empty(strip.Method(method).Calls("_manager.CollapseGroup"));
        }

        // The walk: collapse hides members except the active one, and
        // the same rule in row form (glides) and element form (rig).
        var rows = strip.Method("VisibleRunRows");
        Assert.Single(rows.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString()
                == "group.IsCollapsed && !ReferenceEquals(tab, _manager.ActiveTab)"));
        Assert.Single(rows.Calls("RowElementOf"));

        var tabs = strip.Method("VisibleRunRowTabs");
        Assert.Single(tabs.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString()
                == "group.IsCollapsed && !ReferenceEquals(tab, _manager.ActiveTab)"));
        Assert.Single(tabs.Calls("RowElementOf"));

        // The rig takes its rows from the walk, never from the registry:
        // a shelf row or a hidden member bound to the follow is a row
        // translated to a slot it does not paint.
        var attach = strip.Method("AttachCoDrag");
        Assert.Single(attach.Calls("VisibleRunRows"));
        Assert.Empty(attach.Calls("RowElementOf"));
        Assert.Empty(strip.Method("DetachCoDrag").Calls("RowElementOf"));

        // The glides: one unit, header plus every visible row, rigidly.
        var glide = strip.Method("GlideUnit");
        Assert.Single(glide.Calls("VisibleRunRowTabs"));
        Assert.Single(glide.Calls("GlideHeader"));
        Assert.Equal(2, glide.Calls("GlideRow").Count);
    }

    /// <summary>
    /// The cargo re-arms after every commit -- MoveGroup's churn rebuilds
    /// member containers, and a rebuilt row has neither the translation
    /// nor the accent until re-armed, which would leave members at their
    /// layout slots while the header rides the pointer -- and comes home
    /// on every exit, because ResetDragVisual is the tail both the cut
    /// and the settled path run. The header glides join the row glides
    /// in the one leak census: a ghost the count cannot see is a ghost.
    /// </summary>
    [Fact]
    public void TheCargo_ReArmsAfterEveryCommit_AndComesHomeOnEveryExit()
    {
        var strip = Strip();

        Assert.Single(strip.Method("StartDragVisual").Calls("AttachCoDrag"));

        var rebind = strip.Method("RebindFollow");
        var rearm = rebind.Calls("AttachCoDrag").Single();
        var restart = rebind.Calls("visual.StartAnimation").Single();
        Assert.True(rearm.Span.Start > restart.Span.Start,
            "the stack re-arms after the follow restarts, on the fresh visual");

        var reset = strip.Method("ResetDragVisual");
        var detach = reset.Calls("DetachCoDrag").Single();
        var selection = reset.Calls("UpdateSelectionRow").Single();
        Assert.True(detach.Span.Start < selection.Span.Start,
            "the cargo is handed back before the selection overlay catches up");

        // Both endings hand the cargo back: the !settled cut tail AND
        // the settle batch's completion. The settled call needs its
        // guard spelled out -- a stale settle must not tear down a
        // fresh drag's rig -- and needs pinning at all because a
        // handback that only ever runs from the tail leaves every
        // co-drag row translated and accented after every settled
        // drop, invisible to the ghost census.
        var end = strip.Method("EndDrag");
        var tail = end.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!settled");
        Assert.Contains("ResetDragVisual(drag)", tail.Statement.ToString());
        var completed = end.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "batch.Completed");
        var handler = completed.Right.DescendantNodesAndSelf()
            .OfType<AnonymousFunctionExpressionSyntax>().First();
        var superseded = handler.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                == "_drag is { } live && ReferenceEquals(live.Item, drag.Item)");
        Assert.NotEmpty(superseded.Statement.DescendantNodesAndSelf()
            .OfType<ReturnStatementSyntax>());
        var settledHandback = handler.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "ResetDragVisual");
        Assert.True(superseded.Span.End < settledHandback.Span.Start,
            "the superseded guard must precede the settled handback");

        // The header glides hand back with the rows, in the same sweep.
        var detachMotion = strip.Method("DetachGapMotion");
        Assert.NotEmpty(detachMotion.Calls("HandBackHeader"));
        Assert.NotEmpty(detachMotion.Calls("HandBackRow"));
        var census = strip.Method("CountLeakedMotion").ExpressionBody!.ToString();
        Assert.Contains("_gapMotion.Count", census);
        Assert.Contains("_gapMotionHeaders.Count", census);
        Assert.Contains("_teardownFailures", census);
    }

    /// <summary>
    /// Every exit of the run tick funnels through CancelDrag, the same
    /// discipline the one-row tick follows: Cancel owns the rollback
    /// (pre-drag order replay through the manager) and the EndDrag tail,
    /// and a path that nulls the session itself skips both.
    /// </summary>
    [Fact]
    public void EveryRunTickExit_FunnelsThroughCancel()
    {
        var run = Strip().Method("EvaluateRunDrag");

        // The run tick's exits: the run dissolved, the identity grab
        // missed, and the commit's churn closed the dragged tab out from
        // under the gesture. All three cancel; none of them nulls.
        var cancels = run.Calls("CancelDrag");
        Assert.Equal(3, cancels.Count);
        Assert.All(cancels, c => Assert.Equal("\"closed\"", c.Arg(0)));
        Assert.Empty(run.AssignsTo("_drag"));
    }

    /// <summary>
    /// The tick's measure cannot escape the strip. A container the
    /// projection holds but layout has not realized (config auto-reload,
    /// session restore, a theme flip rebuilding the header mid-drag)
    /// turns MeasureUnitMids into a throw, and this tick runs inside an
    /// uncaught dispatcher callback -- a crash. The one-row tick's
    /// counterpart is never-throwing by construction, and the lift path
    /// catches the same throw into a refusal; the run tick is neither,
    /// so it keeps its belief and returns -- the same no-crossing
    /// outcome an unmeasured unit gets -- and the next tick retries
    /// after relayout.
    /// </summary>
    [Fact]
    public void AnUnrealizedMeasure_CannotEscapeTheRunTick()
    {
        var run = Strip().Method("EvaluateRunDrag");

        // One measure, and it sits inside the guard.
        Assert.Single(run.Calls("MeasureUnitMids"));
        var guarded = run.DescendantNodes().OfType<TryStatementSyntax>()
            .Single(t => t.Block.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText() == "MeasureUnitMids"));
        var catchClause = guarded.Catches.Single();
        Assert.Equal("InvalidOperationException",
            catchClause.Declaration!.Type.ToString());

        // The frame is dropped and nothing else: a return, no state
        // writes, no rethrow -- keep-belief, not a cancellation.
        var handler = catchClause.Block;
        Assert.Single(handler.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>());
        Assert.Empty(handler.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>());
        Assert.Empty(handler.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>());
    }

    /// <summary>
    /// The two toggle gates are deliberately NOT the same predicate. The
    /// pointer gate is phase-aware because a header press now arms a
    /// session immediately, and on a plain click MUXC raises ItemInvoked
    /// from its own release handler -- deeper in the tree, so before the
    /// strip's release handler clears the still-unlifted session: a
    /// session-exists gate would eat every header click. A lifted
    /// gesture holds the pointer capture, so MUXC never raises for it at
    /// all, which is what makes the phase the right boundary. The
    /// command gate stays strict: commands have no unlifted session to
    /// race, and a collapse under a genuinely live drag reorders visible
    /// rows under the pointer.
    /// </summary>
    [Fact]
    public void ThePointerToggle_GateIsTheLiftedPhase_AndTheCommandGateIsNot()
    {
        var strip = Strip();

        var toggle = strip.Method("ToggleGroup");
        var pointerGate = toggle.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Equal("_drag is { Machine.Phase: TabDragPhase.Dragging }",
            pointerGate.Condition.ToString());
        Assert.NotEmpty(pointerGate.Statement.DescendantNodesAndSelf()
            .OfType<ReturnStatementSyntax>());
        Assert.Single(toggle.Calls("_manager.CollapseGroup"));

        var command = strip.Method("ToggleGroupFromCommand");
        Assert.Equal("_drag is not null",
            command.DescendantNodes().OfType<IfStatementSyntax>().First().Condition.ToString());
    }

    /// <summary>
    /// Displaced units glide whole, and the batch identity is the
    /// handback: only units still riding THIS batch come home when it
    /// completes, so a re-glide inside the 250ms window is not killed by
    /// the batch it superseded. The dragged run glides nothing -- it is
    /// the follow's cargo -- and one batch covers the whole tick.
    /// </summary>
    [Fact]
    public void TheRunGapGlides_MoveWholeUnits_AndHandBackByBatchIdentity()
    {
        var glides = Strip().Method("StartRunGapGlides");

        // The dragged run is skipped: its rows translate by the follow.
        var skip = glides.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "ReferenceEquals(unit.Group, dragged)");
        Assert.NotEmpty(skip.Statement.DescendantNodesAndSelf()
            .OfType<ContinueStatementSyntax>());

        // One batch per tick, however many units moved.
        Assert.Equal(1, glides.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(i => i.CalleeText().EndsWith(".CreateScopedBatch", System.StringComparison.Ordinal)));

        // Both leak censuses hand back by batch identity inside the one
        // completion handler.
        var completed = glides.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "settled.Completed");
        var handler = completed.Right.ToString();
        Assert.Contains("ReferenceEquals(g.Value.Batch, settled)", handler);
        Assert.Contains("HandBackRow(entry.Key)", handler);
        Assert.Contains("HandBackHeader(entry.Key)", handler);
        Assert.Single(glides.Calls("settled.End"));

        // The header's glide parks in the header census, keyed by group.
        var header = Strip().Method("GlideHeader");
        var parked = header.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_gapMotionHeaders[group]");
        Assert.Equal("(item, batch)", parked.Right.ToString());
    }

    /// <summary>
    /// The order pass's drift check must expect what the projection
    /// RENDERS. The old formula (tabs minus pinned) predates chip'd
    /// collapses: the rebuild correctly removes a hidden member's row,
    /// dropping the count below the formula, so every pass re-detected
    /// drift and re-ran RebuildAllItems -- a dispatcher-looped rebuild
    /// that spun the UI thread and ballooned the working set.
    /// </summary>
    [Fact]
    public void The_order_drift_check_counts_what_the_projection_renders()
    {
        var strip = Strip();
        var order = strip.Method("ReconcileRowOrder");
        var gate = order.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("_items.Count !=", StringComparison.Ordinal));
        Assert.Contains("_items.Count != shown.Count", gate.Condition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("_manager.Tabs.Count - pinCount", gate.Condition.ToString(), StringComparison.Ordinal);
    }

    // --- The capture-less engine (iv): the host refuses CapturePointer
    // for every gesture -- human hand 5/5 holder=none -- so the vertical
    // runs on hover-routed events exactly like the horizontal. ---

    /// <summary>
    /// The engine holds no capture anywhere. Matched by SUFFIX over parsed
    /// invocations: CapturePointer is an instance method only ever spelled
    /// with a receiver, so a bare-name pin could never go red -- the
    /// vacuous-pin lesson. The one surviving mention is the why-comment in
    /// StartDragVisual, which is prose, not an invocation.
    /// </summary>
    [Fact]
    public void The_vertical_engine_never_captures_the_pointer()
    {
        var strip = Strip();
        Assert.Empty(strip.Root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("CapturePointer", StringComparison.Ordinal)));

        // And no CaptureLost hook either: the engine holds no capture, so
        // no CaptureLost is ours -- the one the strip sees is MUXC's item
        // layer releasing its own press capture the moment a drag starts
        // moving, and acting on that murdered every real drag (the probe
        // caught a cancel mid-drag, then a zombie crossing landing the
        // right order by luck). A re-added hook must go red here.
        Assert.DoesNotContain("PointerCaptureLostEvent", ReadStrip(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A release the strip never saw -- the button coming up off the
    /// strip -- leaves the session behind, and the next press ends it
    /// here rather than blocking every drag after it.
    /// </summary>
    [Fact]
    public void A_stale_session_ends_at_the_next_press()
    {
        var pressed = Strip().Method("OnDragPointerPressed");
        var gate = Assert.IsType<IfStatementSyntax>(pressed.Body!.Statements.First());
        Assert.Equal("_drag is not null", gate.Condition.ToString());
        var arm = gate.Statement;
        Assert.Contains(arm.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "CancelDrag");
        Assert.Equal("stale", arm.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText() == "CancelDrag").Arg(0).Trim('"'));
    }

    /// <summary>
    /// The arm guards that keep clicks clicks: a press under a button
    /// (the close glyph) never arms, and the sub-threshold gate lives in
    /// the machine's Pressed phase -- a release under the threshold
    /// cancels silently, which is the click.
    /// </summary>
    [Fact]
    public void Button_presses_and_sub_threshold_releases_never_arm()
    {
        var pressed = Strip().Method("OnDragPointerPressed");
        var buttonGate = pressed.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains(
                "FindAncestor<Button>", StringComparison.Ordinal));
        Assert.True(
            buttonGate.Statement is ReturnStatementSyntax { Expression: null },
            "A press under a button is the button's: the arm must fall "
            + "through before any session is built.");
    }

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
}
