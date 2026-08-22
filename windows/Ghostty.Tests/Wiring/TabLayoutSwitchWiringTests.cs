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
/// in between. Closing the last tab closes the window, and a storyboard that
/// has already begun still raises Completed, so the callback ran against a
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
    /// <c>_switchStoryboard = null;</c> as a statement in its own right. The
    /// field means "a switch is in flight", so every path that ends one has to
    /// clear it -- the landing, and the Begin that threw before there was ever
    /// anything to land.
    /// </summary>
    private static bool ClearsTheStoryboard(StatementSyntax statement) =>
        statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
        && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
        && assignment.Left.ToString() == "_switchStoryboard"
        && assignment.Right.ToString() == "null";

    /// <summary><c>if (_switchStoryboard is not null)</c> and nothing looser:
    /// an inverted test, or a condition that merely mentions the field, is a
    /// different syntax shape and does not match.</summary>
    private static bool IsSwitchInFlightGuard(StatementSyntax statement)
    {
        if (statement is not IfStatementSyntax guard) return false;
        if (guard.Condition is not IsPatternExpressionSyntax test) return false;
        if (test.Expression is not IdentifierNameSyntax { Identifier.Text: "_switchStoryboard" }) return false;
        if (test.Pattern is not UnaryPatternSyntax negated || !negated.IsKind(SyntaxKind.NotPattern)) return false;
        return negated.Pattern is ConstantPatternSyntax constant
            && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
    }

    /// <summary>
    /// CancelSwitch's one <c>if (_switchStoryboard is not null)</c> statement.
    /// Everything that reads the field lives inside it, and so does the trace
    /// line, whose at-most-once property is that block and nothing else.
    /// </summary>
    private static IfStatementSyntax SwitchInFlightGuard(MethodDeclarationSyntax cancel)
    {
        var found = cancel.Body!.Statements.Where(IsSwitchInFlightGuard).ToList();
        Assert.True(
            found.Count == 1,
            "CancelSwitch must reach _switchStoryboard through exactly one "
            + "`if (_switchStoryboard is not null)` block: a bare _switchStoryboard.Stop() faults on "
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
    public void CancelSwitch_StopsTheStoryboardAndRunsNoEndStateWork()
    {
        var cancel = Coordinator().Method("CancelSwitch");
        var statements = cancel.Body!.Statements;

        var guard = SwitchInFlightGuard(cancel);
        var guarded = Assert.IsType<BlockSyntax>(guard.Statement).Statements;
        var guardIndex = statements.IndexOf(guard);

        // Stop is one half of the defence: a stopped Storyboard does not raise
        // Completed at all, so the landing never gets scheduled. Reached only
        // through the block above -- a switch is in flight on a minority of
        // closes, and the field is null on all the rest.
        Assert.Single(cancel.Calls("_switchStoryboard.Stop"));
        Assert.True(
            guarded.Any(s => s.Calls("_switchStoryboard.Stop").Any()),
            "the Stop must sit inside the null-checked block, not above it");

        // Clearing the field is the other half, and the one Stop cannot
        // provide: a Completed already queued in the same frame as the Stop
        // still runs, and only the identity check in Animate turns it away.
        // Pinned on its own because deleting this line leaves every other
        // assertion in this test green.
        Assert.True(
            guarded.Any(ClearsTheStoryboard),
            "CancelSwitch must clear _switchStoryboard inside the null-checked block");

        // The storyboard block and both releases have to run before the early
        // return: that return is taken when no active-tab morph is in flight,
        // and a switch can stop a storyboard, reveal the pane or park an icon
        // ghost with no morph staged at all.
        var earlyExit = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any())
            .Count();
        Assert.True(earlyExit < statements.Count, "expected CancelSwitch to return early when no morph is staged");
        Assert.True(
            guardIndex < earlyExit,
            "the storyboard must be stopped and cleared before CancelSwitch's early return");

        // Stopping the switch storyboard releases one of the three things a
        // switch has in the air. The pane reveal is a Composition InsetClip
        // animation the compositor keeps driving against the pane host while
        // the window disposes its leaves, and the icon ghost stays parked on
        // the morph layer with both real badges left transparent. Neither is a
        // XAML storyboard, so neither is reachable by Stop.
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
            "_switchStoryboard.Stop",
            "_morphStoryboard?.Stop",
            "FinishIconGhost",
            "CancelPaneReveal",
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
            "the SWITCH cancel line must sit inside the `_switchStoryboard is not null` block, "
            + "which is what keeps a close with no switch in flight from emitting one");
    }

    [Fact]
    public void Animate_ClearsTheStoryboardWhenBeginThrows()
    {
        var animate = Coordinator().Method("Animate");

        // Found by what the try attempts rather than by being the first one:
        // Animate has a second try below for the morph storyboard, and "the
        // first try in the method" swaps silently the day their order changes.
        var attempts = animate.DescendantNodes()
            .OfType<TryStatementSyntax>()
            .Where(t => t.Block.Calls("sb.Begin").Any())
            .ToList();
        Assert.True(attempts.Count == 1, "expected one try around sb.Begin in Animate");
        var catches = attempts[0].Catches;
        Assert.True(catches.Count == 1, "expected one catch on the try around sb.Begin");
        var handler = catches[0].Block.Statements;

        var clearIndex = handler.TakeWhile(s => !ClearsTheStoryboard(s)).Count();
        var finishIndex = handler.TakeWhile(s => !s.Calls("FinishSwitch").Any()).Count();

        // Nothing is in flight after a Begin that threw, and the field claims
        // otherwise until this line runs. Left set, it holds a storyboard that
        // never began for the life of the window: the close then stops it and
        // emits a SWITCH cancel for a switch the fallback below already
        // finished and counted, so a healthy run fails the fuzz oracle.
        Assert.True(
            clearIndex < handler.Count,
            "the catch around sb.Begin must clear _switchStoryboard; a storyboard that never began "
            + "is not a switch in flight, and the close would emit a cancel for one already ended");
        Assert.True(finishIndex < handler.Count, "expected the catch to land the switch without animating it");
        Assert.True(
            clearIndex < finishIndex,
            "the field must be cleared before FinishSwitch, which invokes onCompleted and can stage the "
            + "next switch; clearing after that would null the storyboard that switch just registered");
    }

    [Fact]
    public void SwitchCompleted_CannotLandAfterACancel()
    {
        var coordinator = Coordinator();
        coordinator.Field("_switchStoryboard");

        var animate = coordinator.Method("Animate");
        var body = animate.Body!.Statements;

        // Position, not just presence. Moved into the Completed lambda or into
        // the catch below sb.Begin, the assignment is still a descendant of
        // Animate -- and from the catch no live switch ever registers, so every
        // completion trips the identity check below, FinishSwitch never runs
        // and _switching latches for the life of the window.
        static bool StagesTheStoryboard(StatementSyntax statement) =>
            statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Left.ToString() == "_switchStoryboard"
            && assignment.Right.ToString() == "sb";

        var stageIndex = body.TakeWhile(s => !StagesTheStoryboard(s)).Count();
        var wiringIndex = body
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                          && a.Left.ToString() == "sb.Completed"))
            .Count();

        Assert.True(
            stageIndex < body.Count,
            "Animate must stage `_switchStoryboard = sb` as a statement of its own body");
        Assert.True(wiringIndex < body.Count, "expected Animate to attach a Completed handler to sb");
        Assert.True(
            stageIndex < wiringIndex,
            "the storyboard must be staged before the handler that reads it is attached");

        var completed = animate.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                         && a.Left.ToString() == "sb.Completed");
        var statements = Assert
            .IsType<ParenthesizedLambdaExpressionSyntax>(completed.Right)
            .Block!.Statements;

        // Identity against the field CancelSwitch clears, matched as syntax
        // rather than text: a condition rewritten to a constant, or one that
        // logs instead of returning, is a different shape and does not match.
        //
        // Exactly two operands, and exactly these two. Without that,
        // ReferenceEquals(_switchStoryboard, _switchStoryboard) reads as a
        // guard and never returns, so a cancelled switch lands anyway; and
        // ReferenceEquals(_switchStoryboard, null) reads as a guard and always
        // returns, so the layout toggle is dead for the window's life.
        static bool IsStaleStoryboardGuard(StatementSyntax statement)
        {
            if (statement is not IfStatementSyntax guard) return false;
            if (guard.Condition is not PrefixUnaryExpressionSyntax negation
                || !negation.IsKind(SyntaxKind.LogicalNotExpression)) return false;
            if (negation.Operand is not InvocationExpressionSyntax call
                || call.Expression.ToString() != "ReferenceEquals") return false;
            var operands = call.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
            return operands.Count == 2
                && operands.Contains("_switchStoryboard")
                && operands.Contains("sb")
                && ReturnsEarly(guard);
        }

        // The field is non-null exactly while a switch is in flight, so the
        // landing has to clear it (ClearsTheStoryboard, shared with the
        // Begin-threw path above). Left set, a close an hour later would Stop
        // a long-finished storyboard and release its hold values across both
        // tab hosts, the title bar and the icon transforms, mid-teardown -- by
        // the one method whose premise is that touching that tree is the
        // hazard.
        var guardIndex = statements.TakeWhile(s => !IsStaleStoryboardGuard(s)).Count();
        var clearIndex = statements.TakeWhile(s => !ClearsTheStoryboard(s)).Count();
        var finishIndex = statements
            .TakeWhile(s => !s.Calls("FinishSwitch").Any())
            .Count();

        // All three have to exist, or TakeWhile silently returns the full count
        // and the comparisons pass while pinning nothing.
        Assert.True(
            guardIndex < statements.Count,
            "the Completed handler must bail unless its storyboard is still the current one; "
            + "Stop does not recall a completion already queued this frame");
        Assert.True(
            clearIndex < statements.Count,
            "the landing must clear _switchStoryboard; nothing else does on the normal path");
        Assert.True(finishIndex < statements.Count, "expected the Completed handler to call FinishSwitch");
        Assert.True(guardIndex < clearIndex, "the identity check must run before the field is cleared");
        Assert.True(
            clearIndex < finishIndex,
            "the field must be cleared before FinishSwitch, which invokes onCompleted and can stage the "
            + "next switch; clearing after that would null the storyboard that switch just registered");
    }

    [Fact]
    public void NudgeWindowForImpact_StopsOnTeardownAcrossItsAwaits()
    {
        var method = Window().Method("NudgeWindowForImpact");

        var awaits = method.DescendantNodes().OfType<AwaitExpressionSyntax>().ToList();

        // Load-bearing: with no awaits the per-await loop below proves nothing
        // and passes. The gate matters precisely because this method resumes
        // on later dispatcher turns.
        Assert.NotEmpty(awaits);

        // Control flow, not source order. A gate that merely sits later in the
        // file -- moved out of the loop to below it, say -- satisfies every
        // await by position while the window is moved again on the turn after
        // it closed. The gate has to be the next statement executed.
        foreach (var suspension in awaits)
        {
            var suspending = suspension.FirstAncestorOrSelf<StatementSyntax>();
            Assert.True(suspending is not null, "every await sits inside a statement");
            var block = suspending!.Parent as BlockSyntax;
            Assert.True(
                block is not null,
                "the awaiting statement must sit in a block, so the gate can be the statement after it");

            var next = block!.Statements.IndexOf(suspending!) + 1;
            Assert.True(
                next < block.Statements.Count && IsClosedGuard(block.Statements[next]),
                "the statement right after each await must be the _isClosed gate; the window can close "
                + "while the nudge is suspended and the next step moves it again");
        }

        // And once before it starts, as a statement of the method itself: an
        // entry gate nested inside some other branch is not one.
        var entry = method.Body!.Statements.FirstOrDefault(IsClosedGuard);
        Assert.True(
            entry is not null,
            "the impact nudge keeps moving AppWindow after teardown starts unless it is gated on _isClosed");
        Assert.True(
            entry!.SpanStart < awaits[0].SpanStart,
            "the nudge must also bail before it starts moving an already-closing window");
    }
}
