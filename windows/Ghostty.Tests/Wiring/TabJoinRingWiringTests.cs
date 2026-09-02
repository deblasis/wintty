using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The hold-with-a-ring join gesture's wiring, in BOTH strips.
///
/// The decision this guards is that the gesture exists twice and means
/// one thing: a tab held over its neighbour until a ring fills is
/// released into a group, in the sidebar and in the tab strip alike. The
/// model half -- when the ring completes, which neighbour it points at,
/// what a join commits -- is executed in TabJoinDwellTests and
/// TabJoinDropTests. What only source can answer is whether each strip
/// actually reaches those decisions from its own drag tick and its own
/// release, and whether the two do it the same way; a strip that quietly
/// stopped calling the dwell would leave every model test green and the
/// gesture gone from that layout.
///
/// Wiring guards, not behaviour tests. Whether the ring is legible, or
/// lands on the right pixel, is only observable on a live drag -- the
/// seam's drag-join op and the tab-drag harness are what exercise that.
/// </summary>
public class TabJoinRingWiringTests
{
    private static ShellSource Vertical() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    private static ShellSource Horizontal() => ShellSource.Load("Tabs.TabHost.xaml.cs");

    /// <summary>
    /// Each strip advances the ring from its own drag tick. The vertical
    /// strip's tick is the coalesced EvaluateDrag, the horizontal's is
    /// the pointer move that evaluates crossings; either way the dwell
    /// has to be fed the SAME dragged center the crossings were judged
    /// against, or the ring would point at a row the engine does not
    /// think the drag is over.
    /// </summary>
    [Fact]
    public void BothStrips_AdvanceTheDwell_FromTheirDragTick()
    {
        var evaluate = Vertical().Method("EvaluateDrag");
        var verticalFeed = evaluate.Call("UpdateJoinDwell");
        Assert.Equal("draggedCenter", verticalFeed.Arg(1));

        var moved = Horizontal().Method("OnStripPointerMoved");
        var horizontalFeed = moved.Call("UpdateJoinDwell");
        Assert.Equal("draggedCenter", horizontalFeed.Arg(1));
    }

    /// <summary>
    /// The ring is never drawn where the release would refuse: both
    /// strips ask CanJoin before they hold the dwell, and a refusal
    /// clears rather than falling through. This is the no-false-promise
    /// rule the pin ghost already obeys -- the ring is the only thing
    /// telling the user what the release is about to mean, and a ring
    /// over a pinned row would be a lie the commit then has to break.
    /// </summary>
    [Fact]
    public void BothStrips_AskCanJoin_BeforeTheyRing()
    {
        foreach (var update in new[]
                 {
                     Vertical().Method("UpdateJoinDwell"),
                     Horizontal().Method("UpdateJoinDwell"),
                 })
        {
            var canJoin = update.Call("TabJoinDrop.CanJoin");
            var hold = update.Call("_joinDwell.Hold");
            Assert.True(
                canJoin.Span.Start < hold.Span.Start,
                "the eligibility question must be asked before the ring starts filling");

            // The refusal has to be an exit, not a note: a CanJoin whose
            // false arm fell through would ring on every row.
            var guard = update.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(i => i.Condition.ToString().Contains("TabJoinDrop.CanJoin"));
            AssertClearsAndReturns(guard);
        }
    }

    /// <summary>
    /// Both strips pick their target through the shared Core mapping,
    /// with the SAME band token. Two strips picking neighbours by their
    /// own arithmetic is how one gesture becomes two that merely look
    /// alike, and a band re-derived per strip is how one of them silently
    /// gets a different reach.
    /// </summary>
    [Fact]
    public void BothStrips_PickTheirTarget_ThroughTheSharedMappingAndToken()
    {
        foreach (var update in new[]
                 {
                     Vertical().Method("UpdateJoinDwell"),
                     Horizontal().Method("UpdateJoinDwell"),
                 })
        {
            var pick = update.Call("TabJoinDrop.PickTarget");
            Assert.Equal("TabStripMotion.JoinBandFraction", pick.Arg(3));
        }
    }

    /// <summary>
    /// The release forks on the ARM, and the target it joins is the one
    /// the ring was drawn on -- read back off the dwell, never re-derived
    /// from the release point. The promise was made to a specific row,
    /// and a hand that drifted a pixel in the last frame must not move it.
    /// </summary>
    [Fact]
    public void BothStrips_JoinOnTheArm_ToTheTargetTheRingNamed()
    {
        foreach (var release in new[]
                 {
                     Vertical().Method("DragRelease"),
                     Horizontal().Method("FinishHorizontalDrag"),
                 })
        {
            var fork = release.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(i => i.Condition.ToString().Contains("_joinDwell.IsArmed"));
            Assert.Contains("_joinDwell.Target is TabModel", fork.Condition.ToString());

            var join = release.Call("TabJoinDrop.Join");
            Assert.True(
                join.Span.Start > fork.Condition.Span.Start
                    && join.Span.End < fork.Span.End,
                "the join must be inside the armed fork, not a call the release always makes");
        }
    }

    /// <summary>
    /// In the vertical strip the join fork stands AHEAD of the pin arms.
    /// Both are release-classified and both return, so whichever comes
    /// first wins outright -- and a row over the pinned shelf never rings
    /// (UpdateJoinDwell stands down on a live pin preview), so the two
    /// cannot both be earned. Ordering them this way keeps the exclusive
    /// pair from depending on which arm happened to be written first.
    /// </summary>
    [Fact]
    public void TheVerticalJoin_StandsAheadOfThePinArms()
    {
        var release = Vertical().Method("DragRelease");
        var join = release.Call("TabJoinDrop.Join");
        var setPinned = release.Calls("_manager.SetPinned");
        Assert.NotEmpty(setPinned);
        Assert.True(
            setPinned.All(call => call.Span.Start > join.Span.Start),
            "the join fork must be reached before the pin-out and pin-drop arms");
    }

    /// <summary>
    /// A pinned row and a run drag never ring. Groups cannot be pinned,
    /// so the prefix outranks membership; and a run landing inside a run
    /// is a different op with its own grammar, not something this
    /// gesture may guess at. A live pin preview stands the ring down too,
    /// in the strip that has one: two promises over one release is how a
    /// gesture starts lying.
    /// </summary>
    [Fact]
    public void NeitherStripRings_ForAPinnedRowOrARunDrag()
    {
        var vertical = Vertical().Method("UpdateJoinDwell");
        var verticalGuard = vertical.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("drag.Group is not null"));
        Assert.Contains("drag.Tab.IsPinned", verticalGuard.Condition.ToString());
        Assert.Contains("_pinPreview is not null", verticalGuard.Condition.ToString());
        AssertClearsAndReturns(verticalGuard);

        var horizontal = Horizontal().Method("UpdateJoinDwell");
        var horizontalGuard = horizontal.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("drag.Group is not null"));
        // The OPERAND, not a substring of the whole condition: "IsPinned" is
        // equally present in "!dragged.IsPinned", which rings on exactly the
        // rows this guard exists to refuse.
        Assert.Contains(
            horizontalGuard.Condition.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .Select(m => m.ToString()),
            s => s == "dragged.IsPinned");
        AssertClearsAndReturns(horizontalGuard);
    }

    /// <summary>
    /// The guard withdraws the dwell and leaves, asserted as nodes.
    ///
    /// As text it was Contains("ClearJoinDwell") and Contains("return"), and
    /// both are substrings: the first is satisfied by ClearJoinDwellTypo, and
    /// the second by any identifier with "return" in it -- or by a "return"
    /// that is not the guard's own. What has to be true is that this arm calls
    /// the clear AND leaves, since clearing without leaving falls through into
    /// the hold and re-arms the thing just withdrawn.
    /// </summary>
    private static void AssertClearsAndReturns(IfStatementSyntax guard)
    {
        Assert.Single(guard.Statement.Calls("ClearJoinDwell"));
        Assert.NotEmpty(guard.Statement.DescendantNodesAndSelf()
            .OfType<ReturnStatementSyntax>());
    }

    /// <summary>
    /// Every gesture ending clears the dwell. An armed ring that outlived
    /// its drag would be read by the NEXT release, which is a group the
    /// user never asked for from a gesture they never made -- and the
    /// vertical strip's EndDrag is the one funnel drop, cancel, escape
    /// and teardown all pass through, so clearing it there covers them
    /// all.
    /// </summary>
    [Fact]
    public void EveryGestureEnding_ClearsTheDwell()
    {
        Assert.NotNull(Vertical().Method("EndDrag").Call("ClearJoinDwell"));
        Assert.NotNull(Horizontal().Method("CancelHorizontalDrag").Call("ClearJoinDwell"));
        Assert.NotNull(Horizontal().Method("FinishHorizontalDrag").Call("ClearJoinDwell"));
    }

    /// <summary>
    /// Stated as the invariant rather than as a list of doors: EVERY place that
    /// nulls the drag takes the ring with it.
    ///
    /// Naming three methods covers the three that existed when it was written.
    /// The vertical strip has two more exits that null `_drag` without going
    /// through `EndDrag` -- `DragRelease`'s click path and `CancelDrag`'s
    /// non-dragging arm -- and a clear was added to the second of those in this
    /// change with nothing to hold it there. A sweep is the only form of this
    /// rule that survives the next exit being added.
    /// </summary>
    [Fact]
    public void VerticalStrip_EveryPathThatNullsTheDrag_TakesTheRingWithIt()
    {
        var strip = Vertical();
        var nulls = strip.Root.AssignsTo("_drag")
            .Where(a => a.Right is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression })
            .ToList();
        Assert.NotEmpty(nulls);

        foreach (var assignment in nulls)
        {
            var owner = assignment.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            Assert.True(owner is not null, "a _drag = null outside any method");

            // The enclosing BLOCK, not the whole method. Method scope makes the
            // rule vacuous the moment a method has two exits: CancelDrag's
            // early arm can drop its clear entirely and still pass, because the
            // method's OTHER path calls EndDrag further down. The arm that
            // nulls the drag is the arm that has to withdraw the promise.
            var arm = assignment.FirstAncestorOrSelf<BlockSyntax>();
            Assert.True(arm is not null, "a _drag = null outside any block");

            var clears = arm!.Calls("ClearJoinDwell").Count > 0
                || arm.Calls("EndDrag").Count > 0
                || owner!.Identifier.ValueText == "EndDrag";
            Assert.True(
                clears,
                $"'{owner!.Identifier.ValueText}' nulls the drag on a path that neither "
                + "withdraws the join ring nor hands off to EndDrag, so an armed promise "
                + "outlives the gesture that made it and the NEXT release reads it");
        }
    }

    /// <summary>
    /// The halo's FORM is chosen on every call, above the arm/disarm edge.
    ///
    /// SetArmed opens with `if (armed == _armed) return;`, so anything below
    /// that runs only when the armed state changes. High Contrast can be turned
    /// on at any moment -- including inside a hold, while the ring is being
    /// placed at 16ms and `armed` is not changing -- and behind the edge the
    /// translucent tint stays over the target row's title for the rest of the
    /// dwell, which is the exact state the High Contrast branch exists to
    /// forbid.
    /// </summary>
    [Fact]
    public void TheHaloForm_IsChosenAboveTheArmEdge()
    {
        var setArmed = ShellSource.Load("Tabs.TabJoinRing.cs").Method("SetArmed");
        var form = Assert.Single(setArmed.Calls("SetHaloForm"));
        var edge = setArmed.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("_armed", StringComparison.Ordinal));
        Assert.True(
            form.SpanStart < edge.SpanStart,
            "the halo's form is chosen below the armed-state early return, so a High "
            + "Contrast flip inside a hold leaves the wash over the row's title");
    }

    /// <summary>
    /// The ring fills on a repeating timer, on the shared frame token.
    /// A ring advanced only by pointer moves could never complete: the
    /// dwell's own premise is a pointer that has stopped, and a stopped
    /// pointer raises nothing.
    /// </summary>
    [Fact]
    public void BothStrips_FillTheRingOnATimer_NotOnPointerEvents()
    {
        foreach (var start in new[]
                 {
                     Vertical().Method("StartJoinTimer"),
                     Horizontal().Method("StartJoinTimer"),
                 })
        {
            Assert.Contains(
                "TabStripMotion.JoinRingTickMs",
                start.Call("TimeSpan.FromMilliseconds").Arg(0));
            Assert.Single(start.AssignsTo("_joinTimer").Where(
                a => a.Right.ToString() == "timer"));
            Assert.NotNull(start.Call("timer.Start"));

            // REPEATING, which is the whole claim. A DispatcherQueueTimer is
            // one-shot by default, so dropping this line ships a ring that
            // advances exactly once and then waits for a pointer event that,
            // by the dwell's own premise, never comes -- "a ring advanced only
            // by pointer moves could never complete", which is the sentence
            // this test is named after and did not check. AssignsTo cannot see
            // it: the left side is a member access, not a bare identifier.
            var repeating = start.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == "IsRepeating")
                .ToList();
            Assert.True(
                repeating.Count == 1 && repeating[0].Right.ToString() == "true",
                "the join timer is not set repeating, so the ring advances once and stops");

            // And it refuses to start a second clock. Without the early return
            // every pointer move during a hold mints a fresh 16ms timer and
            // leaks all but the last, which is a pile of clocks all filling one
            // ring.
            var reentry = start.DescendantNodes().OfType<IfStatementSyntax>()
                .FirstOrDefault(i => IsEarlyReturn(i.Statement)
                    && i.Condition.ToString().Contains("_joinTimer", StringComparison.Ordinal));
            Assert.True(
                reentry is not null && reentry.SpanStart < start.Call("timer.Start").SpanStart,
                "StartJoinTimer does not refuse a second timer before creating one");

            // And with the right POLARITY. "_joinTimer is null" also mentions
            // _joinTimer and also returns early -- and means the timer is never
            // created at all, so the ring never advances. Existence and
            // position are not the property; "there is already one" is.
            Assert.Equal(
                "_joinTimer is not null", reentry!.Condition.ToString());
        }
    }

    /// <summary>
    /// The TICK re-asks whether the join is still possible, before advancing.
    ///
    /// This is the one that matters, and it is the one the pointer-driven
    /// guards structurally cannot cover. The dwell's premise is that no pointer
    /// event arrives for 450ms -- that is what it is measuring -- so the
    /// pointer path's CanJoin check is not consulted once during the window
    /// that decides the gesture. Without this the ring completed on a pair the
    /// release then refused: the target pinned by an accelerator mid-hold,
    /// gathered into the dragged tab's own group by another actor, or closed.
    ///
    /// And it must stand BEFORE the hold, or the frame that armed is the frame
    /// nobody checked.
    /// </summary>
    [Fact]
    public void BothStrips_ReAskCanJoinOnEveryTick_BeforeAdvancingTheDwell()
    {
        foreach (var tick in new[]
                 {
                     Vertical().Method("TickJoinDwell"),
                     Horizontal().Method("TickJoinDwell"),
                 })
        {
            var guard = tick.DescendantNodes().OfType<IfStatementSyntax>()
                .FirstOrDefault(i => i.Condition.ToString()
                    .Contains("TabJoinDrop.CanJoin", StringComparison.Ordinal));
            Assert.True(
                guard is not null,
                "the dwell tick advances without re-asking whether the join is still "
                + "possible, so the 450ms that decides the gesture is unchecked");

            var negation = Assert.IsType<PrefixUnaryExpressionSyntax>(guard!.Condition);
            Assert.True(
                negation.IsKind(SyntaxKind.LogicalNotExpression),
                "the tick's CanJoin guard is not negated, so it withdraws the dwell "
                + "exactly when the join IS possible");
            AssertClearsAndReturns(guard);

            var hold = Assert.Single(tick.Calls("_joinDwell.Hold"));
            Assert.True(
                guard.SpanStart < hold.SpanStart,
                "the check runs after the dwell has already advanced, so the frame "
                + "that armed is the frame nobody checked");
        }
    }

    /// <summary>An if-body that is a return, braced or not.</summary>
    private static bool IsEarlyReturn(StatementSyntax body)
        => body is ReturnStatementSyntax
            || (body is BlockSyntax block
                && block.Statements.Count == 1
                && block.Statements[0] is ReturnStatementSyntax);

    /// <summary>
    /// The seam's join op pins a virtual clock for the length of the
    /// gesture and puts it back on every path out. Sleeping the real
    /// dwell instead would time the scheduler rather than the ring, and a
    /// virtual clock left behind would freeze the ring for every later
    /// gesture in the process -- arming it on the first frame or never.
    /// </summary>
    [Fact]
    public void TheSeamJoinOp_DrivesTheDwellClock_AndAlwaysRestoresIt()
    {
        var walker = Vertical().Method("TestSeamDragJoinAsync");
        Assert.Empty(walker.Calls("Task.Delay"));

        var tryStatement = walker.DescendantNodes().OfType<TryStatementSyntax>().Single();
        Assert.NotNull(tryStatement.Finally);
        Assert.Contains("_seamJoinClockMs = null", tryStatement.Finally!.ToString());

        // The hold is one assignment to that clock, past the dwell token,
        // followed by the tick that reads it.
        var advance = walker.AssignsTo("_seamJoinClockMs")
            .Single(a => a.Right.ToString().Contains("TabStripMotion.JoinDwellMs"));
        var tick = walker.Call("TickJoinDwell");
        Assert.True(
            advance.Span.Start < tick.Span.Start,
            "the clock must be advanced before the tick that reads it");

        // The release happens under the seam's clock, not after the
        // finally has handed the wall clock back.
        var release = walker.Call("DragRelease");
        Assert.True(
            release.Span.Start < tryStatement.Finally.Span.Start,
            "the release must be inside the pinned-clock block");
    }

    /// <summary>
    /// The seam op refuses a pair the commit would refuse and a pair that
    /// is not adjacent, rather than walking a gesture that cannot mean
    /// anything: the ring only ever targets a neighbour, so a driver
    /// asking for a distant row is asking for a gesture the product does
    /// not have. Adjacency is judged in SLOT space, because a collapsed
    /// run's hidden members hold manager indices and no slots.
    /// </summary>
    [Fact]
    public void TheSeamJoinOp_RefusesAPairTheGestureCannotMean()
    {
        var walker = Vertical().Method("TestSeamDragJoinAsync");
        Assert.NotNull(walker.Call("TabJoinDrop.CanJoin"));
        var adjacency = walker.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("fromSlot - toSlot"));
        Assert.Contains("!= 1", adjacency.Condition.ToString());
        Assert.NotNull(walker.Call("DragSlots"));
    }
}
