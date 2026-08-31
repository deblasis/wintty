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
        // The parameterized press: the pointer handler and the test seam
        // both resolve values OUTSIDE this method; everything from the row
        // resolution down -- the header arm included -- lives in DragPress.
        var pressed = Strip().Method("DragPress");
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
        var pressed = Strip().Method("DragPress");
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
        var pressed = Strip().Method("DragPress");
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

    /// <summary>
    /// The drift gate's rebuild is the last resort, and its worst failure
    /// mode was landing inside MUXC's still-open container realization
    /// (the state spans frames with virtualized hosts). The gate must
    /// hand the rebuild to the retry executor -- which yields off the
    /// foreign frame and re-queues -- instead of running it bare on
    /// whatever frame the drift was detected on.
    /// </summary>
    [Fact]
    public void The_drift_gate_defers_its_rebuild_off_the_foreign_frame()
    {
        var order = Strip().Method("ReconcileRowOrder");
        var gate = order.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("_items.Count != shown.Count", StringComparison.Ordinal));
        var retry = gate.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "ReconcileRetry.Rebuild");
        Assert.True(
            gate.SpanStart < retry.Span.Start,
            "The drift gate must route its rebuild through the retry: a bare "
            + "rebuild lands on whatever frame detected the drift, and that "
            + "frame can be MUXC's own container realization.");
        Assert.Equal("RebuildAllItems", retry.Arg(1));
    }

    /// <summary>
    /// PIN-OUT is RELEASE-CLASSIFIED: a row the drag pinned mid-gesture
    /// ends where the user let go. Released over the shelf/zone: stay
    /// pinned (the in-zone landing). Released outside: unpin and place
    /// at the body slot under the release point. The position is fresh
    /// pointer truth -- never machine centers, whose staleness after a
    /// pin is the trap this replaces. The mid-drag crossing arm PINS only:
    /// an Unpin classification there rewinds and refuses, so the one-
    /// grammar contract (pin-in by crossing, out by release) is true in
    /// bytes.
    /// </summary>
    [Fact]
    public void Pin_out_is_release_classified()
    {
        // The parameterization moved the release body into DragRelease;
        // the fresh pointer truth is the Y the pointer handler resolved,
        // so the chain is pinned at both ends: the wrapper reads the
        // event's position, and the pin branch takes that Y as its own.
        var wrapper = Strip().Method("OnDragPointerReleased");
        Assert.Single(wrapper.Calls("e.GetCurrentPoint"));
        var released = Strip().Method("DragRelease");
        var gate = released.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "drag.Tab.IsPinned");
        var arm = Assert.IsType<BlockSyntax>(gate.Statement);

        // The branch reads fresh pointer truth and the shelf bounds: the
        // release Y the wrapper resolved, the body slot under the point
        // from the strip's own pairing.
        var releaseY = arm.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "releaseY");
        Assert.Equal("y", releaseY.Initializer!.Value.ToString());
        Assert.Contains(arm.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "BodySlotAtY").ToList(),
            c => true);

        // The out-of-zone arm unpins and places -- never a bare keep.
        Assert.Contains("SetPinned(drag.Tab, false);", arm.ToFullString(),
            StringComparison.Ordinal);

        // POLARITY: the SetPinned(false) invocation sits INSIDE the
        // !inZone gate -- the unpin fires only when the release point is
        // provably outside the shelf bounds. Hoisted out (always-unpin)
        // or inverted (unpin only when in-zone), both go red here. The
        // mutation record must be verified in the same run as the claim.
        var unpin = arm.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "_manager.SetPinned");
        var polarityGate = unpin.Ancestors().OfType<IfStatementSyntax>()
            .First(a => a.Condition.ToString() == "!inZone");
        Assert.True(
            polarityGate.SpanStart < unpin.SpanStart,
            "the unpin must sit inside the !inZone gate: the polarity is the "
            + "fix -- hoisted out it always unpins, inverted it never does.");

        // The inZone initializer is the second prong: the ancestors-pin
        // cannot catch a VALUE-level flip (swapping >=/<= inside inZone's
        // initializer still passes everything). The initializer must
        // compare releaseY against BOTH shelf bounds.
        Assert.Contains("releaseY >= shelfTop && releaseY <= shelfBottom",
            arm.DescendantNodes()
               .OfType<VariableDeclaratorSyntax>()
               .Single(v => v.Identifier.ValueText == "inZone")
               .Initializer!.Value.ToFullString(),
            StringComparison.Ordinal);

        // The mid-drag crossing arm PINS only: the gate is the Pin
        // classification, and the Unpin classification hangs off it as an
        // else that rewinds the machine to the row's still-true slot and
        // refuses the crossing. Release classification owns the out, so
        // no unpin may fire mid-drag.
        var evaluated = Strip().Method("EvaluateDrag");
        var crossingGate = evaluated.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "zone.Op == TabPinZoneOp.Pin");
        var refuse = Assert.IsType<IfStatementSyntax>(crossingGate.Else!.Statement);
        Assert.Equal("zone.Op == TabPinZoneOp.Unpin", refuse.Condition.ToString());
        Assert.Contains(refuse.Statement.DescendantNodes().OfType<BreakStatementSyntax>().ToList(),
            b => true);
        Assert.Contains("drag.Machine.UpdateIndex(crossing.From);",
            refuse.Statement.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SetPinned(drag.Tab, false)", crossingGate.ToFullString(),
            StringComparison.Ordinal);
    }

    // --- The churn crash's named root (dump 2026-08-31, artifacts) ------
    // The fail-fast's stowed COMException (800F1000, "Cannot apply a Style
    // with TargetType NavigationViewItem to an object of type
    // ContentControl") was raised from set_SelectedItem reached through
    // SyncSelectionFromManager <- ReconcileRetry.Rebuild <- ReconcileRowOrder
    // <- ScheduleReconcile: a rebuild writes MenuItems, MUXC realizes the
    // containers on the NEXT layout pass, and the selection assignment in
    // that window resolves a base-class ContentControl container for the
    // selected item. The fix makes the assignment wait out realization.

    /// <summary>
    /// The selection assignment may not run while the selected item's
    /// container is still unrealized. The guard is polarity-sensitive in
    /// exactly the way a flip survives compilation: inverted, the sync
    /// runs only in the crashing window and defers once the strip is
    /// healthy -- the strip renders and every churn still throws. The
    /// landed path clears the defer flag before it assigns, so the
    /// realization latch knows the sync landed.
    /// </summary>
    [Fact]
    public void TheSelectionSync_WaitsOutContainerRealization()
    {
        var sync = Strip().Method("SyncSelectionFromManager");

        var gate = sync.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "item is not null && !item.IsLoaded");
        Assert.NotEmpty(gate.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>().Where(c => c.CalleeText() == "DeferSelectionSync"));

        var assignment = sync.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "NavView.SelectedItem");
        Assert.Equal("item", assignment.Right.ToString());
        Assert.True(
            gate.Span.End < assignment.Span.Start,
            "the realization guard must precede the selection assignment");

        // The flag clears only on the path that assigns: a sync that
        // deferred again leaves the latch armed.
        var cleared = sync.AssignsTo("_selectionSyncDeferred")
            .Single(a => a.Right.ToString() == "false");
        Assert.True(
            gate.Span.End < cleared.Span.Start,
            "the defer flag must not clear before the realization gate");

        // And the never-loaded fence still defers through the same flag:
        // the pre-template hazard that predates this fix keeps its guard.
        var unloaded = sync.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!IsLoaded");
        Assert.Contains("true", unloaded.Statement.AssignsTo("_selectionSyncDeferred")
            .Single().Right.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The latch is one subscription, re-armed by deferring again: the
    /// defer subscribes the pass handler under the not-already-latched
    /// guard, every pass attempts the sync once, and the handler detaches
    /// on the first pass after the sync landed -- including teardown, so a
    /// strip that is going away does not keep a handler for passes it
    /// will never attend.
    /// </summary>
    [Fact]
    public void TheRealizationLatch_RidesTheItem_AndDetachesWhenLanded()
    {
        var defer = Strip().Method("DeferSelectionSync");
        var guard = defer.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                == "ReferenceEquals(_selectionRealizationItem, item)");
        Assert.True(guard.Statement is ReturnStatementSyntax);
        // Event subscriptions parse as assignments, not invocations: the
        // latch rides the deferred item's own Loaded -- the realization
        // event -- never a strip-rooted per-pass handler.
        var subscribe = defer.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "item.Loaded").ToList();
        Assert.Equal(2, subscribe.Count);
        Assert.All(subscribe, a => Assert.Equal(
            "OnSelectionRealized", a.Right.ToString()));
        Assert.True(
            guard.Span.End < subscribe[0].Span.Start,
            "the not-this-item guard must precede the resubscription");

        var realized = Strip().Method("OnSelectionRealized");
        var ifRealized = Assert.IsType<IfStatementSyntax>(
            realized.Body!.Statements.First());
        var block = Assert.IsType<BlockSyntax>(ifRealized.Statement);
        var detach = Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(block.Statements.First()).Expression);
        Assert.Equal("item.Loaded", detach.Left.ToString());
        Assert.Equal("OnSelectionRealized", detach.Right.ToString());
        var clear = realized.AssignsTo("_selectionRealizationItem").Single();
        Assert.Equal("null", clear.Right.ToString());
        var retry = Assert.IsType<ExpressionStatementSyntax>(realized.Body!.Statements.Last());
        Assert.Equal("SyncSelectionFromManager",
            Assert.IsType<InvocationExpressionSyntax>(retry.Expression).CalleeText());
        var realizedClear = realized.AssignsTo("_selectionSyncDeferred")
            .Single(a => a.Right.ToString() == "false");
        Assert.True(realizedClear.Span.End < retry.Span.Start,
            "the handler must clear the flag before the attempt, or the "
            + "detach can never fire");

        // And the strip stays free of a second standing LayoutUpdated
        // latch: the realization wait lives on the item, not on every
        // pass anywhere in the window.
        Assert.Equal(1, Strip().Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Count(a => a.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddAssignmentExpression)
                && a.Left.ToString() == "LayoutUpdated"));
    }
}
