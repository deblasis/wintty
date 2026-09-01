using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal/vertical tab switch lands on a completion callback about
/// 340ms after it starts, and nothing cancelled it when the window went away
/// in between. Closing the last tab closes the window, and an animation that
/// has already begun still raises its completion, so the callback ran against a
/// window that was tearing down. crash.log recorded four NullReferenceExceptions
/// with this stack before the gate was added:
///
///   ApplyButtonColors -> ApplyCaptionButtonChrome -> RefreshTabHostChrome
///   -> AnimateTabLayoutTo's completion -> LayoutCoordinator.FinishSwitch
///
/// The gate is _isClosed, not a null AppWindow. AppWindow survives into
/// OnClosedAsync, so it goes null strictly later than teardown starts, and in
/// that gap the theme manager is disposed and panes are being freed. These
/// tests pin the earlier signal deliberately, because a later one leaves the
/// gap open.
///
/// That gate is only the last thing the completion reaches, though, so the
/// switch is now cancelled at the source instead and the gate stays as the
/// backstop. LayoutCoordinator.CancelSwitch is where the reasoning lives --
/// what a switch still has in the air, why the landing cannot simply be
/// fast-forwarded, and what is deliberately left alone. The tests below pin
/// both halves, because either alone is one edit away from being the only one.
///
/// These are wiring guards. They prove the gate is on the path a closing
/// window takes; whether the window survives a switch is only observable live.
/// </summary>
public class TabLayoutSwitchWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    private static ShellSource Coordinator() => ShellSource.Load("Shell.LayoutCoordinator.cs");

    /// <summary>
    /// The lambda passed as <c>onCompleted:</c>, found by argument name rather
    /// than by being the only lambda present, so adding another callback to
    /// the method does not turn this into an unreadable sequence error.
    /// </summary>
    private static ParenthesizedLambdaExpressionSyntax SwitchCompletion()
    {
        var animate = Window().Method("AnimateTabLayoutTo");
        var arg = animate.DescendantNodes()
            .OfType<ArgumentSyntax>()
            .Single(a => a.NameColon?.Name.Identifier.Text == "onCompleted");
        return Assert.IsType<ParenthesizedLambdaExpressionSyntax>(arg.Expression);
    }

    private static bool ReturnsEarly(IfStatementSyntax guard) =>
        guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();

    /// <summary><c>if (_isClosed) return;</c> and nothing that merely mentions
    /// the field: an inverted or dead condition is a different syntax shape and
    /// does not match.</summary>
    private static bool IsClosedGuard(StatementSyntax statement) =>
        statement is IfStatementSyntax
        {
            Condition: IdentifierNameSyntax { Identifier.Text: "_isClosed" }
        } guard
        && ReturnsEarly(guard);

    /// <summary>
    /// <c>_timeline = null;</c> as a statement in its own right. The field
    /// means "a switch is in flight", so every path that ends one has to
    /// clear it -- the landing, and the Begin that threw before there was
    /// ever anything to land.
    /// </summary>
    private static bool ClearsTheTimeline(StatementSyntax statement) =>
        statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
        && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
        && assignment.Left.ToString() == "_timeline"
        && assignment.Right.ToString() == "null";

    /// <summary><c>if (_timeline is not null)</c> and nothing looser:
    /// an inverted test, or a condition that merely mentions the field, is a
    /// different syntax shape and does not match.</summary>
    private static bool IsSwitchInFlightGuard(StatementSyntax statement)
    {
        if (statement is not IfStatementSyntax guard) return false;
        if (guard.Condition is not IsPatternExpressionSyntax test) return false;
        if (test.Expression is not IdentifierNameSyntax { Identifier.Text: "_timeline" }) return false;
        if (test.Pattern is not UnaryPatternSyntax negated || !negated.IsKind(SyntaxKind.NotPattern)) return false;
        return negated.Pattern is ConstantPatternSyntax constant
            && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
    }

    /// <summary>
    /// CancelSwitch's one <c>if (_timeline is not null)</c> statement.
    /// Everything that reads the field lives inside it, and so does the trace
    /// line, whose at-most-once property is that block and nothing else.
    /// </summary>
    private static IfStatementSyntax SwitchInFlightGuard(MethodDeclarationSyntax cancel)
    {
        var found = cancel.Body!.Statements.Where(IsSwitchInFlightGuard).ToList();
        Assert.True(
            found.Count == 1,
            "CancelSwitch must reach _timeline through exactly one "
            + "`if (_timeline is not null)` block: a bare _timeline.Release() faults on "
            + "every close with no switch in flight, and a trace line outside the block reports a "
            + "cancel for a switch that already emitted its end");
        return (IfStatementSyntax)found[0];
    }

    [Fact]
    public void SwitchCompletion_BailsOutOnceTeardownHasStarted()
    {
        var completion = SwitchCompletion();
        var statements = completion.Block!.Statements;

        var guard = statements.FirstOrDefault(IsClosedGuard);

        Assert.True(
            guard is not null,
            "the layout-switch completion must return early once _isClosed is set; "
            + "a null AppWindow is a strictly later signal and leaves the teardown gap open");

        // Order matters rather than position: everything below the gate is
        // what touches disposed state.
        var guardIndex = statements.IndexOf(guard!);
        var refreshIndex = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression.ToString() == "RefreshTabHostChrome"))
            .Count();

        Assert.True(refreshIndex < statements.Count, "expected a RefreshTabHostChrome call to guard");
        Assert.True(guardIndex < refreshIndex, "the teardown gate must precede the chrome work");
    }

    [Fact]
    public void ApplyButtonColors_ChecksBothHalvesBeforeDereferencing()
    {
        var method = Window().Method("ApplyButtonColors");

        // The crash line reads AppWindow.TitleBar, which faults whether
        // AppWindow or TitleBar is null, so the check has to cover both. A
        // plain `AppWindow is null` test leaves the second half live.
        var guard = method.Body!.Statements
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s =>
                s.Condition is IsPatternExpressionSyntax pattern &&
                pattern.Expression.ToString().Contains("AppWindow?.TitleBar"));

        Assert.True(
            guard is not null,
            "ApplyButtonColors must check AppWindow?.TitleBar, covering a null window and a null title bar");
        Assert.True(ReturnsEarly(guard!), "the guard must return");
    }

    [Fact]
    public void ApplyButtonColors_GuardsBeforeMutatingState()
    {
        var method = Window().Method("ApplyButtonColors");
        var statements = method.Body!.Statements;

        var guardIndex = statements
            .TakeWhile(s => s is not IfStatementSyntax i ||
                            !i.Condition.ToString().Contains("AppWindow"))
            .Count();

        var cacheIndex = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "_lastButtonColors"))
            .Count();

        // Both have to exist, or TakeWhile silently returns the full count and
        // the comparison passes while pinning nothing.
        Assert.True(guardIndex < statements.Count, "expected an AppWindow guard");
        Assert.True(cacheIndex < statements.Count, "expected a _lastButtonColors assignment");
        Assert.True(guardIndex < cacheIndex, "the guard must run before any state is mutated");
    }

    [Fact]
    public void WindowTeardown_CancelsTheLayoutSwitch()
    {
        var statements = Window().Method("OnClosedAsync").Body!.Statements;

        var cancelIndex = statements
            .TakeWhile(s => !s.Calls("_layout.CancelSwitch").Any())
            .Count();
        Assert.True(
            cancelIndex < statements.Count,
            "OnClosedAsync must cancel an in-flight layout switch; the 340ms landing outlives the close");

        // Unconditionally. A call is found anywhere inside the statement that
        // holds it, so `if (IsQuickTerminal) { _layout.CancelSwitch(); }` reads
        // as a cancel while every regular window closes without one.
        Assert.True(
            statements[cancelIndex] is ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call
            }
            && call.Expression.ToString() == "_layout.CancelSwitch",
            "the cancel must be a statement of OnClosedAsync itself, not nested inside a condition");

        // Anchored at both ends. _isClosed is what makes the completion inert
        // if one is already queued, so it has to be set first; the theme
        // manager is the first disposal in the method and the first state a
        // landing switch would read through RefreshTabHostChrome.
        var gateIndex = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                          && a.Left.ToString() == "_isClosed"
                          && a.Right.ToString() == "true"))
            .Count();
        var disposeIndex = statements
            .TakeWhile(s => !s.Calls("_themeManager.Dispose").Any())
            .Count();

        // Both have to exist, or TakeWhile silently returns the full count and
        // the comparison passes while pinning nothing.
        Assert.True(gateIndex < statements.Count, "expected OnClosedAsync to set _isClosed");
        Assert.True(disposeIndex < statements.Count, "expected the theme manager to be disposed on close");
        Assert.True(
            gateIndex < cancelIndex,
            "_isClosed must be set before the cancel, so a completion already queued this frame is turned away too");
        Assert.True(
            cancelIndex < disposeIndex,
            "the switch must be cancelled before the state its landing reads is disposed");
    }

    [Fact]
    public void CancelSwitch_ReleasesTheTimelineAndRunsNoEndStateWork()
    {
        var cancel = Coordinator().Method("CancelSwitch");
        var statements = cancel.Body!.Statements;

        var guard = SwitchInFlightGuard(cancel);
        var guarded = Assert.IsType<BlockSyntax>(guard.Statement).Statements;
        var guardIndex = statements.IndexOf(guard);

        // Release is one half of the defence, and it is the half that
        // matters most now: the timeline's expressions are not
        // self-terminating -- they run until stopped, however finite the
        // drivers are -- and the compositor would keep driving them against
        // a tree the close is disposing. Reached only through the block
        // above -- a switch is in flight on a minority of closes, and the
        // field is null on all the rest. Without end-value writes: rest
        // values on a closing window are work for nobody.
        Assert.Single(cancel.Calls("_timeline.Release"));
        Assert.True(
            guarded.Any(s => s.Calls("_timeline.Release").Any()),
            "the Release must sit inside the null-checked block, not above it");

        // Clearing the field is the other half, and the one Release cannot
        // provide: a landing already queued in the same frame as the stop
        // still runs, and only the identity check in Animate turns it away.
        // Pinned on its own because deleting this line leaves every other
        // assertion in this test green.
        Assert.True(
            guarded.Any(ClearsTheTimeline),
            "CancelSwitch must clear _timeline inside the null-checked block");

        // The accent tail's timeline is released too, outside the guard: it
        // outlives its own switch by design, so it can be live when no
        // switch is. Matched as the null-conditional call the source spells.
        Assert.Single(cancel.Calls("_tail?.Release"));

        // The timeline block and both releases have to run before the early
        // return: that return is taken when no active-tab morph is in flight,
        // and a switch can start a timeline, reveal the pane or park an icon
        // ghost with no morph staged at all.
        var earlyExit = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any())
            .Count();
        Assert.True(earlyExit < statements.Count, "expected CancelSwitch to return early when no morph is staged");
        Assert.True(
            guardIndex < earlyExit,
            "the timeline must be released and cleared before CancelSwitch's early return");

        // Releasing the timeline stops what it registered. The pane
        // reveal's clip still has to come off the pane host's visual, and
        // the icon ghost stays parked on the morph layer with both real
        // badges left transparent; neither is the timeline's to remove.
        //
        // CancelPaneReveal, not FinishPaneReveal: only the clip has to come
        // off here. See CancelSwitch's summary for why the margin stays put.
        //
        // Each has to be a statement of CancelSwitch itself. A call is found
        // anywhere beneath the statement holding it, so `if (_iconGhost is not
        // null) FinishIconGhost();` reads as a release while any other shape
        // of that condition skips it.
        foreach (var release in new[] { "FinishIconGhost", "CancelPaneReveal" })
        {
            Assert.Single(cancel.Calls(release));
            var index = statements.TakeWhile(s => !s.Calls(release).Any()).Count();
            Assert.True(index < statements.Count, $"CancelSwitch must call {release}");
            Assert.True(
                statements[index] is ExpressionStatementSyntax
                {
                    Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call
                }
                && call.Expression.ToString() == release,
                $"{release} must be a statement of CancelSwitch itself, not nested inside a condition");
            Assert.True(
                index < earlyExit,
                $"{release} must run before CancelSwitch's early return, which is taken when no morph is in flight");
        }

        // What this method may call, spelled the way the source spells it so
        // that a bare "Stop" cannot stand in for a Stop on another receiver.
        //
        // An allow-list rather than a list of forbidden names. Snap,
        // FinishSwitch and FinishActiveTabMorph are the three that must never
        // appear -- they walk both tab hosts, the vertical title bar and the
        // pane host, which is the tree a closing window is disposing -- but
        // naming only those waves through an inlined copy of Snap's body, a
        // RefreshTabHostChrome, or the same work under a name invented later.
        // Adding an entry here is meant to cost a decision, weighed against
        // what this method's summary says it is allowed to touch.
        var allowed = new[]
        {
            "MorphTrace",
            // The switch's expressions and the accent tail's, stopped
            // without end-value writes: the compositor keeps driving an
            // unstopped expression against a tree the close is disposing,
            // and rest values on that tree are work for nobody. Release
            // walks no tab host and writes no element.
            "_timeline.Release",
            "_tail?.Release",
            "FinishIconGhost",
            "CancelPaneReveal",
            // The ghost's box is a Composition Scale on one visual and an
            // InsetClip sweep on another, released twice on purpose: the
            // timeline stops what it registered, and this stops the same
            // animations by property name for the interrupt paths that
            // never had a timeline. Required below as well as allowed
            // here, because an allow-list entry on its own only stops the
            // guard complaining -- it does not make the release happen.
            "morph.Ghost.StopBoxAnimations",
        };
        var unexpected = cancel.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(SyntaxQueries.CalleeText)
            .Where(n => !allowed.Contains(n, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(
            unexpected.Count == 0,
            "CancelSwitch must run no end-state work, found: " + string.Join(", ", unexpected));

        // The ghost's compositor animations, released after the early
        // return because they belong to the morph the early return is
        // looking for. Asserted the same way the two releases above are: a
        // statement of CancelSwitch itself, so `if (something)
        // morph.Ghost.StopBoxAnimations();` cannot stand in for it.
        Assert.Single(cancel.Calls("morph.Ghost.StopBoxAnimations"));
        var stopBoxIndex = statements
            .TakeWhile(s => !s.Calls("morph.Ghost.StopBoxAnimations").Any())
            .Count();
        Assert.True(
            stopBoxIndex < statements.Count,
            "CancelSwitch must stop the ghost's box animations; they run on the compositor against a "
            + "visual whose tree the close is disposing");
        Assert.True(
            statements[stopBoxIndex] is ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } stopBoxCall
            }
            && stopBoxCall.Expression.ToString() == "morph.Ghost.StopBoxAnimations",
            "the box release must be a statement of CancelSwitch itself, not nested inside a condition");
        Assert.True(
            stopBoxIndex > earlyExit,
            "the box release reads the morph, so it has to sit after the early return that proves there is one");

        // A morph still waiting for its destination holds a LayoutUpdated
        // handler on the morph root and a deadline on
        // CompositionTarget.Rendering. Rendering is a thread-level event, so a
        // pending handler keeps the closed window's whole tree alive until the
        // thread renders again -- the leak CancelStripPriming already exists
        // to close, reachable by a second route.
        //
        // Right-hand sides too: `_morphRoot.LayoutUpdated -= null;` detaches
        // nothing and satisfies a check that reads only the event name.
        var detached = cancel.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
            .Select(a => (Event: a.Left.ToString(), Handler: a.Right.ToString()))
            .ToList();
        Assert.Contains(("_morphRoot.LayoutUpdated", "morph.Waiting"), detached);
        Assert.Contains(("CompositionTarget.Rendering", "morph.WaitingDeadline"), detached);
    }

    [Fact]
    public void CancelSwitch_LeavesATraceTheFuzzOracleCanPair()
    {
        var cancel = Coordinator().Method("CancelSwitch");

        // Animate emits SWITCH begin and FinishSwitch emits SWITCH end, and
        // the morph fuzz harness fails the run when those two counts differ.
        // A cancelled switch never reaches FinishSwitch, so without a line of
        // its own every close landing inside a switch reads as a switch that
        // never finished.
        var traced = cancel.Calls("MorphTrace").Select(c => c.Arg(0)).ToList();
        Assert.Single(traced);
        Assert.StartsWith("\"SWITCH cancel", traced[0], StringComparison.Ordinal);

        // The harness reads any ghosts= above zero as a leaked ghost, and a
        // cancel deliberately leaves the morph ghost on a tree about to be
        // destroyed, so the line must not carry one.
        Assert.DoesNotContain("ghosts=", traced[0], StringComparison.Ordinal);

        // Inside the null-checked block, which is the whole of what makes the
        // line at most one per switch rather than one per close. Emitted
        // unconditionally, a window closed with no switch in flight still
        // reports a cancel, and the harness subtracts it from a begin its own
        // end already answered: a healthy run fails the oracle.
        var guarded = Assert.IsType<BlockSyntax>(SwitchInFlightGuard(cancel).Statement).Statements;
        Assert.True(
            guarded.Any(s => s.Calls("MorphTrace").Any()),
            "the SWITCH cancel line must sit inside the `_timeline is not null` block, "
            + "which is what keeps a close with no switch in flight from emitting one");
    }

    [Fact]
    public void Animate_ClearsTheTimelineWhenStagingThrows()
    {
        var animate = Coordinator().Method("Animate");

        // Found by what the try attempts rather than by position: Animate
        // has a second try around the timeline's construction, and "the
        // first try in the method" swaps silently the day their order
        // changes. This one wraps the staging and Begin -- a throw anywhere
        // in it means expressions may already be live, so the catch must
        // release as well as clear.
        var attempts = animate.DescendantNodes()
            .OfType<TryStatementSyntax>()
            .Where(t => t.Block.Calls("timeline.Begin").Any())
            .ToList();
        Assert.True(attempts.Count == 1, "expected one try around timeline.Begin in Animate");
        var catches = attempts[0].Catches;
        Assert.True(catches.Count == 1, "expected one catch on the try around timeline.Begin");
        var handler = catches[0].Block.Statements;

        var clearIndex = handler.TakeWhile(s => !ClearsTheTimeline(s)).Count();
        var releaseIndex = handler.TakeWhile(s => !s.Calls("timeline.Release").Any()).Count();
        var finishIndex = handler.TakeWhile(s => !s.Calls("FinishSwitch").Any()).Count();

        // Nothing is in flight after a staging that threw, and the field
        // claims otherwise until this line runs. Left set, it holds a
        // timeline that never began for the life of the window: the close
        // then releases it and emits a SWITCH cancel for a switch the
        // fallback below already finished and counted, so a healthy run
        // fails the fuzz oracle.
        Assert.True(
            clearIndex < handler.Count,
            "the catch around timeline.Begin must clear _timeline; a timeline that never began "
            + "is not a switch in flight, and the close would emit a cancel for one already ended");
        Assert.True(
            releaseIndex < handler.Count,
            "the catch must release the timeline: expressions started before the throw are live "
            + "on the compositor and are not self-terminating");
        Assert.True(finishIndex < handler.Count, "expected the catch to land the switch without animating it");
        Assert.True(
            clearIndex < finishIndex,
            "the field must be cleared before FinishSwitch, which invokes onCompleted and can stage the "
            + "next switch; clearing after that would null the timeline that switch just registered");
    }

    [Fact]
    public void SwitchLanding_CannotLandAfterACancel()
    {
        var coordinator = Coordinator();
        coordinator.Field("_timeline");
        coordinator.Field("_tail");

        var animate = coordinator.Method("Animate");
        var body = animate.Body!.Statements;

        // Position, not just presence. Moved into the landing lambda or into
        // a catch, the assignment is still a descendant of Animate -- and
        // from a catch no live switch ever registers, so every landing trips
        // the identity check below, FinishSwitch never runs and _switching
        // latches for the life of the window.
        static bool StagesTheTimeline(StatementSyntax statement) =>
            statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Left.ToString() == "_timeline"
            && assignment.Right.ToString() == "timeline";

        var stageIndex = body.TakeWhile(s => !StagesTheTimeline(s)).Count();
        var beginIndex = body
            .TakeWhile(s => !s.Calls("timeline.Begin").Any())
            .Count();

        Assert.True(
            stageIndex < body.Count,
            "Animate must stage `_timeline = timeline` as a statement of its own body");
        Assert.True(beginIndex < body.Count, "expected Animate to call timeline.Begin");
        Assert.True(
            stageIndex < beginIndex,
            "the timeline must be staged before the landing callback that reads it is attached");

        // The landing callback, found by the argument name Begin spells.
        var begin = animate.Call("timeline.Begin");
        var landed = begin.ArgumentList.Arguments
            .Single(a => a.NameColon?.Name.Identifier.Text == "landed");
        var statements = Assert
            .IsType<ParenthesizedLambdaExpressionSyntax>(landed.Expression)
            .Block!.Statements;

        // Identity against the field CancelSwitch clears, matched as syntax
        // rather than text: a condition rewritten to a constant, or one that
        // logs instead of returning, is a different shape and does not match.
        //
        // Exactly two operands, and exactly these two. Without that,
        // ReferenceEquals(_timeline, _timeline) reads as a guard and never
        // returns, so a cancelled switch lands anyway; and
        // ReferenceEquals(_timeline, null) reads as a guard and always
        // returns, so the layout toggle is dead for the window's life.
        static bool IsStaleTimelineGuard(StatementSyntax statement)
        {
            if (statement is not IfStatementSyntax guard) return false;
            if (guard.Condition is not PrefixUnaryExpressionSyntax negation
                || !negation.IsKind(SyntaxKind.LogicalNotExpression)) return false;
            if (negation.Operand is not InvocationExpressionSyntax call
                || call.Expression.ToString() != "ReferenceEquals") return false;
            var operands = call.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
            return operands.Count == 2
                && operands.Contains("_timeline")
                && operands.Contains("timeline")
                && ReturnsEarly(guard);
        }

        var guardIndex = statements.TakeWhile(s => !IsStaleTimelineGuard(s)).Count();
        var clearIndex = statements.TakeWhile(s => !ClearsTheTimeline(s)).Count();
        var completeIndex = statements
            .TakeWhile(s => !s.Calls("timeline.CompleteSwitchPhase").Any())
            .Count();
        var finishIndex = statements
            .TakeWhile(s => !s.Calls("FinishSwitch").Any())
            .Count();

        // All four have to exist, or TakeWhile silently returns the full count
        // and the comparisons pass while pinning nothing.
        Assert.True(
            guardIndex < statements.Count,
            "the landing must bail unless its timeline is still the current one; "
            + "stopping the drivers does not recall a completion already queued this frame");
        Assert.True(
            clearIndex < statements.Count,
            "the landing must clear _timeline; nothing else does on the normal path");
        Assert.True(
            completeIndex < statements.Count,
            "the landing must run CompleteSwitchPhase: stop each switch-phase expression and write its "
            + "end value through to the client-side property. This is the landing invariant made "
            + "explicit -- client-side values tell nothing about what a stopped expression held, so "
            + "correctness must not depend on the two agreeing by accident");
        Assert.True(finishIndex < statements.Count, "expected the landing to call FinishSwitch");
        Assert.True(guardIndex < clearIndex, "the identity check must run before the field is cleared");
        Assert.True(
            completeIndex < finishIndex,
            "the write-through must run before FinishSwitch, whose Snap writes element state over "
            + "visual ground that has to already agree with it");
        Assert.True(
            clearIndex < finishIndex,
            "the field must be cleared before FinishSwitch, which invokes onCompleted and can stage the "
            + "next switch; clearing after that would null the timeline that switch just registered");
    }

    private static ShellSource Timeline() => ShellSource.Load("Shell.LayoutSwitchTimeline.cs");

    /// <summary>
    /// This replaces ImpactNudge_IsStoppedOnTeardownRatherThanMerelyNotStarted.
    ///
    /// The impact is no longer a separately scheduled animation the window
    /// owns; it is a term of the incoming strip's Translation expression on
    /// the switch timeline, armed where the ghost's flight stages. The
    /// hazard the old test guarded did not go away, it got a single owner:
    /// every animation the timeline starts must be registered so Release
    /// can stop it, because expressions are not self-terminating and a
    /// finite driver only guarantees the cleanup TURN arrives, not the
    /// cleanup itself. These assertions pin that discipline in the source.
    /// </summary>
    [Fact]
    public void Timeline_EveryAnimationIsRegisteredAndBatchesScopeOnlyDrivers()
    {
        var timeline = Timeline();

        // Every StartAnimation lives in Register, Begin, or SpinIcon (whose
        // extra start is the pivot definition its own WriteEnd stops --
        // that exception is asserted below rather than waved through). A
        // start anywhere else is an animation Release cannot reach: the
        // leak the spring experiment measured, reachable again.
        foreach (var call in timeline.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith(".StartAnimation", StringComparison.Ordinal)))
        {
            var method = call.Ancestors().OfType<MethodDeclarationSyntax>().First();
            Assert.True(
                method.Identifier.ValueText is "Register" or "Begin" or "SpinIcon",
                $"StartAnimation outside Register/Begin/SpinIcon, in {method.Identifier.ValueText}: "
                + "unregistered animations outlive the switch");
        }

        // SpinIcon's pivot expression is stopped by the entry it rides on.
        var spin = timeline.Method("SpinIcon");
        Assert.NotEmpty(spin.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "visual.StopAnimation"));

        // Begin scopes exactly its two drivers, T and S, one per batch.
        // An expression started between CreateScopedBatch and End never
        // completes, so its batch never fires and neither the landing nor
        // the tail cleanup ever runs -- the exact shape of the spring
        // failure. Two batches, two starts, and both starts are on the
        // property set rather than on any visual.
        var begin = timeline.Method("Begin");
        Assert.Equal(2, begin.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(c => c.CalleeText() == "_compositor.CreateScopedBatch"));
        var starts = begin.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith(".StartAnimation", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, starts.Count);
        Assert.All(starts, s => Assert.Equal("_props.StartAnimation", s.CalleeText()));

        // The releases drain through one shape: stop, then optionally write
        // the end value. Release covers both phase lists.
        var releaseEntries = timeline.Method("ReleaseEntries");
        Assert.NotEmpty(releaseEntries.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "entry.Target.StopAnimation"));
        var release = timeline.Method("Release");
        Assert.Equal(2, release.Calls("ReleaseEntries").Count);
    }

    /// <summary>
    /// The leader margin, asserted where it now lives.
    ///
    /// The filmstrip's state oracle used to watch the two hosts' element
    /// opacities live and assert that at no instant both were more than
    /// half present. The fades ride compositor expressions now, and the
    /// spike measured that animated composition values read from the UI
    /// thread are stale -- the oracle cannot see them. So the property is
    /// asserted here over the authored curves instead, against the same
    /// constants the expressions are built from, extracted from the source
    /// rather than copied so this test drifts with the product or not at
    /// all. The film remains the witness that the fades render; see the
    /// filmstrip harness for that half.
    /// </summary>
    [Fact]
    public void LeaderMargin_HoldsOverTheAuthoredCurves()
    {
        static double Const(ShellSource source, string name)
        {
            var field = source.Field(name);
            var text = field.Variable.Initializer!.Value.ToString();
            return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        var coordinator = Coordinator();
        var delay = Const(coordinator, "IncomingFadeDelay");
        var outEnd = Const(coordinator, "OutgoingFadeEnd");

        // The authored curves: FadeIn is ease-out cubic over the delayed
        // ramp, FadeOut drops as (1-u)^3 and is gone by outEnd. The same
        // algebra LayoutSwitchTimeline builds its expression strings from.
        static double EaseOutCubic(double u) => 1 - Math.Pow(1 - u, 3);
        double In(double t) => EaseOutCubic(Math.Clamp((t - delay) / (1 - delay), 0, 1));
        double Out(double t) => Math.Pow(1 - Math.Clamp(t / outEnd, 0, 1), 3);

        for (var t = 0.0; t <= 1.0; t += 0.001)
        {
            Assert.False(
                In(t) > 0.5 && Out(t) > 0.5,
                $"at T={t:F3} both strips are more than half present (in={In(t):F3}, out={Out(t):F3}): "
                + "the cross-fade has no leader");
        }

        // The margin, not just the property: outgoing passes below half
        // strictly before incoming passes above it. Erosion of the gap is
        // a choreography change someone should be making on purpose.
        var outBelow = 0.0;
        while (Out(outBelow) > 0.5) outBelow += 0.001;
        var inAbove = 0.0;
        while (In(inAbove) < 0.5 && inAbove < 1) inAbove += 0.001;
        Assert.True(
            inAbove - outBelow > 0.1,
            $"the leader margin narrowed to {inAbove - outBelow:F3} of the switch "
            + $"(outgoing below half at {outBelow:F3}, incoming above at {inAbove:F3})");
    }
}

