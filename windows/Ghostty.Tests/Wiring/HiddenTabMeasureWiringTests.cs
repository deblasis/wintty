using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A hidden pane's arrange is not a measurement. Two live shapes make
/// that true. A restored background tab enters the tree collapsed and
/// never arranges at all, so its panel reads zero until it is first
/// shown; and a pane that was arranged while visible and collapsed again
/// keeps that stale arrange as its positive read -- a size the pane is
/// not showing in that era. Every creation driver that trusts the
/// panel's current read (the first-layout settle gate, the Loaded
/// fallback, the retry tick) births the pty at the wrong grid in the
/// second shape: the shell formats its startup output for a size the
/// pane never shows, and the #1159 equality reds on a tab that was born
/// behind another one (`pty 79x28 (800x600 px), pane 255x46` in the
/// birth-coverage record).
///
/// The zero-size gate cannot see this class: the placeholder arrange is
/// positive, so SurfacePixelSize.Initial accepts it. What refuses it is
/// effective visibility -- the same discriminator the creation focus
/// guard already uses. A pane that is not effectively visible has no
/// honest measurement yet: its creation waits for the pass its first
/// show brings, and a pass that arrives while it is hidden counts for
/// nothing -- not toward the settle gate, not as a size push, and not as
/// the one retirement of the first-layout handler.
///
/// Refusing is only half the fix: every ordinary driver can be CONSUMED
/// by a hidden era (the one-shot handler returns at the guard, the one-
/// shot Loaded fallback ticks once while hidden, the retry timer arms
/// only on a real refusal), so a pane whose whole birth happened behind
/// the active tab would sit surface-less forever after being shown --
/// nothing owes it a pass anymore. Each hidden pass therefore also arms
/// the show-path backstop (OnLayoutShowWatch), and the pane's first
/// effectively-visible pass settles a still-pending creation outright,
/// or re-pushes the size/scale pair for a surface that already exists.
/// A hidden era delays the work; it does not lose it.
///
/// Every fact here asserts CONTAINMENT or CONDITION, never order alone:
/// a guard must TERMINATE its branch and the work it refuses must sit
/// OUTSIDE that branch; a settle attempt must live INSIDE the branch
/// that owns it; a census must count call sites across the whole shell
/// corpus by callee so no spelling can hide one. Span comparisons only
/// ever SUPPLEMENT a containment or condition assert in the same fact.
///
/// One platform premise the whole design bets on -- that
/// FrameworkElement.LayoutUpdated IS delivered to elements under
/// COLLAPSED ancestors (unlike SizeChanged, which NotificationHost
/// documents as never firing for a collapsed element) -- is pinned
/// live against the real runtime by
/// Ghostty.Tests.Windows/CollapsedAncestorLayoutDeliveryTests.cs, not
/// by anything in this file: no parse of this source can fail if the
/// platform changes under it. The verbatim red text of every mutant
/// these facts were proven against lives in
/// HiddenTabMeasureMutantReceipts.md beside this file.
/// </summary>
public class HiddenTabMeasureWiringTests
{
    private static ShellSource Terminal() =>
        ShellSource.Load("Controls.TerminalControl.xaml.cs");

    private static IfStatementSyntax? GuardBefore(
        MethodDeclarationSyntax method, string later) =>
        method.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>()
            .Where(s => s.Condition.ToString().Contains("!IsEffectivelyVisible()"))
            .FirstOrDefault(s => s.SpanStart < SpanOf(method, later));

    private static int SpanOf(MethodDeclarationSyntax method, string text)
    {
        var token = method.Body!.DescendantTokens()
            .FirstOrDefault(t => t.ToString() == text);
        Assert.True(token != default,
            $"expected to find '{text}' in {method.Identifier.ValueText}");
        return token.SpanStart;
    }

    /// <summary>
    /// A refusal whose branch does not return lets the very work it
    /// refuses still run below it: GuardBefore finds the if, this pins
    /// that the if actually stops the pass. Every mutant that empties a
    /// guard (or deletes only its return) keeps the guard findable and
    /// re-opens the placeholder path behind it.
    /// </summary>
    private static void AssertGuardReturns(IfStatementSyntax guard, string what)
    {
        var terminates =
            guard.Statement is ReturnStatementSyntax
            || (guard.Statement is BlockSyntax block
                && block.Statements.OfType<ReturnStatementSyntax>().Any());
        Assert.True(terminates,
            what + ": the `!IsEffectivelyVisible()` branch must RETURN, not " +
                "fall through: without the return the hidden pane records " +
                "the era and then does the refused work anyway");
    }

    /// <summary>
    /// An if whose condition refuses <paramref name="field"/>, matched on
    /// the parsed shape so the spelling (`!flag` or `flag == false`)
    /// cannot decide the verdict. An INVERTED condition is not a refusal
    /// of the flag and must not match.
    /// </summary>
    private static bool RefusesField(IfStatementSyntax s, string field)
    {
        if (s.Condition is PrefixUnaryExpressionSyntax
            {
                OperatorToken: { RawKind: (int)SyntaxKind.ExclamationToken },
            } not
            && not.Operand is IdentifierNameSyntax id
            && id.Identifier.ValueText == field)
        {
            return true;
        }

        if (s.Condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression,
            } eq)
        {
            var operands = new[] { eq.Left.ToString(), eq.Right.ToString() };
            return operands.Contains(field) && operands.Contains("false");
        }

        return false;
    }

    /// <summary>
    /// An if whose condition compares the surface handle against
    /// IntPtr.Zero, matched on the parsed shape so the operand order
    /// cannot decide the verdict.
    /// </summary>
    private static bool RefusesZeroHandle(IfStatementSyntax s)
    {
        if (s.Condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression,
            } eq)
        {
            return false;
        }

        var operands = new[] { eq.Left.ToString(), eq.Right.ToString() };
        return operands.Any(o => o.Contains("_surface", StringComparison.Ordinal))
            && operands.Any(o => o.Contains("IntPtr.Zero", StringComparison.Ordinal));
    }

    /// <summary>
    /// The creation gate must refuse an invisible pane's arrange with the
    /// same return-false it gives an unmeasured one, in ONE condition:
    /// splitting the refusal into a second if after the tuple pattern
    /// would still create at the placeholder whenever the arrange is
    /// positive, which is the whole defect.
    /// </summary>
    [Fact]
    public void CreationGate_RefusesAnInvisiblePaneArrange()
    {
        var create = Terminal().Method("TryCreateSurface");

        // Non-vacuity: the measured gate itself is still here, and the
        // refusal sits IN its condition.
        var measuredGate = Assert.Single(create.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>(),
            s => s.Condition.ToString().Contains("SurfacePixelSize.Initial"));

        Assert.Contains("!IsEffectivelyVisible()",
            measuredGate.Condition.ToString());
        Assert.True(measuredGate.Condition is BinaryExpressionSyntax,
            "the visibility refusal must sit IN the measured gate's condition "
                + "(`|| !IsEffectivelyVisible()`), not in a statement of its own");
        AssertGuardReturns(measuredGate, "TryCreateSurface's measured gate");
    }

    /// <summary>
    /// A layout pass while the pane is hidden must count for nothing in
    /// the first-layout handler: not toward the settle gate (a repeated
    /// placeholder is not a settled size), not as the size push, and not
    /// as the handler's one retirement. The guard sits before all three
    /// and TERMINATES, and its branch's one action is arming the
    /// show-path backstop: the arming must live INSIDE the branch, so a
    /// pass the guard refuses is the only thing that arms it.
    /// </summary>
    [Fact]
    public void FirstLayoutHandler_IgnoresPassesWhileHidden()
    {
        var handler = Terminal().Method("OnFirstLayoutUpdated");

        // Non-vacuity: the work the guard must refuse is still here.
        Assert.Single(handler.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "CreationMeasureSettled");
        Assert.Single(handler.Body!.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                 && a.Left.ToString() == "Panel.LayoutUpdated");

        var guard = GuardBefore(handler, "CreationMeasureSettled");
        Assert.True(guard is not null,
            "OnFirstLayoutUpdated needs an `!IsEffectivelyVisible()` return "
                + "before the settle gate: a hidden pass must not latch a "
                + "placeholder arrange as the settled measurement");
        AssertGuardReturns(guard!, "OnFirstLayoutUpdated's hidden-pass guard");
        Assert.True(guard!.SpanStart < SpanOf(handler, "LayoutUpdated"),
            "the hidden-pass guard must also precede the handler's "
                + "retirement, or a placeholder pass retires the one-shot "
                + "and the pane's first visible pass goes unheard");

        // Containment: the starvation record belongs to the refused pass.
        // Hoisted out of the branch it would arm the backstop on visible
        // passes too, and every visible pass would re-run the flip body.
        var arming = Assert.Single(handler.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "true");
        Assert.True(guard.Contains(arming),
            "the starvation record must sit INSIDE the hidden-pass guard's "
                + "branch: a record hoisted above the guard arms the backstop "
                + "on every pass and re-baselines every visible pane");
        var branchReturn = guard.Statement is ReturnStatementSyntax r ? r
            : guard.Statement is BlockSyntax b
                ? b.Statements.OfType<ReturnStatementSyntax>().First()
                : null;
        Assert.True(branchReturn is not null && arming.SpanStart < branchReturn.SpanStart,
            "the starvation record must precede the branch's return: a record "
                + "placed after an unconditional return never runs");
    }

    /// <summary>
    /// PushSurfaceSize is the resize path for every later push (size
    /// changes, composition-scale changes, the deferred-flush re-push).
    /// While the pane is hidden its panel carries the placeholder slot,
    /// and pushing that would resize the grid to a size the pane never
    /// shows and relay it as a measurement. The refusal must sit inside
    /// PushSurfaceSize itself -- one guard, every caller -- not at the
    /// callers, which multiply.
    /// </summary>
    [Fact]
    public void PushSurfaceSize_RefusesWhileHidden()
    {
        var push = Terminal().Method("PushSurfaceSize");

        // Non-vacuity: the push itself is still here.
        Assert.Single(push.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetSize");

        var guard = GuardBefore(push, "SurfaceSetSize");
        Assert.True(guard is not null,
            "PushSurfaceSize needs an `!IsEffectivelyVisible()` return "
                + "before SurfaceSetSize: a hidden pane's placeholder slot "
                + "must not be pushed as the pane's size");
        AssertGuardReturns(guard!, "PushSurfaceSize's hidden refusal");
    }

    /// <summary>
    /// The retry budget counts attempts, not ticks, and the tick must skip
    /// without spending while the pane is invisible: the visibility gate in
    /// TryCreateSurface would refuse the attempt anyway, and spending the
    /// budget on refusals would leave a pane the user hid mid-retry without
    /// its surface once the budget ran out -- and the retry timer is the
    /// last driver standing for a pane whose reparent ate the Loaded
    /// fallback, so it must stay able to drive the creation when the pane
    /// is shown again.
    /// </summary>
    [Fact]
    public void RetryTick_SkipsWithoutSpendingWhileHidden()
    {
        var tick = Terminal().Method("OnSurfaceRetryTick");

        // Non-vacuity: the attempt itself is still here.
        Assert.Single(tick.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TrySettleSurfaceCreation");

        var guard = GuardBefore(tick, "TrySettleSurfaceCreation");
        Assert.True(guard is not null,
            "OnSurfaceRetryTick needs an `!IsEffectivelyVisible()` skip "
                + "before the attempt: a hidden pane's ticks must not burn "
                + "the retry budget on refusals");
        AssertGuardReturns(guard!, "OnSurfaceRetryTick's hidden skip");

        // And the skip precedes the SPEND, not only the attempt: the budget
        // is the decrement, and ticks that spend while hidden leave a pane
        // the user hid mid-retry without its surface once the budget runs
        // out - the exact loss the skip exists to prevent.
        Assert.True(guard!.SpanStart < SpanOf(tick, "_surfaceRetryAttemptsLeft"),
            "the hidden-pass skip must precede the budget spend "
                + "(`_surfaceRetryAttemptsLeft--`): a reorder that spends "
                + "before refusing burns the budget the pane needs after it "
                + "is shown again");
    }

    /// <summary>
    /// The show-path backstop is armed at construction and never removed.
    /// The creation handler is a one-shot that retires itself, so a watch
    /// bolted onto IT would retire with it -- but a pane can be hidden at
    /// any point after its creation (the user switches tabs), and the
    /// size/scale re-push that a hidden era owes is deliverable only by a
    /// handler still listening when the pane is next shown. Its lifetime
    /// is the control's, not the creation's.
    /// </summary>
    [Fact]
    public void ShowWatch_IsArmedForTheControlsLifetime()
    {
        var terminal = Terminal();

        var ctor = terminal.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TerminalControl");
        Assert.Contains(ctor.Body!.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                 && a.Left.ToString() == "Panel.LayoutUpdated"
                 && a.Right.ToString() == "OnLayoutShowWatch");

        // And nothing ever unsubscribes it: a `-=` in some cleanup path
        // would end the backstop for every era that starts after it.
        Assert.Empty(terminal.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                        && a.Right.ToString() == "OnLayoutShowWatch"));
    }

    /// <summary>
    /// The flip gate: the watch's body runs only for a pane a hidden era
    /// actually starved. Without the gate every visible pass consumes the
    /// flag, drops the creation measure and re-runs the flip body -- the
    /// measure reset alone would re-baseline the two-pass settle for
    /// every pane in the app, and the settle would never fire for ANY of
    /// them. Deleting the gate, emptying it, or inverting it must all be
    /// visible here.
    /// </summary>
    [Fact]
    public void ShowWatch_TheFlipBodyRunsOnlyAfterAHiddenEra()
    {
        var watch = Terminal().Method("OnLayoutShowWatch");

        // Condition: exactly one if refuses the starved flag, and its
        // refusal is a RETURN. Deletion leaves nothing to find; an
        // inverted condition is not a refusal; an emptied body falls
        // through and runs the flip on every visible pass.
        var flipGate = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>(),
            s => RefusesField(s, "_creationStarvedByHiddenEra"));
        AssertGuardReturns(flipGate, "the flip gate (`if (!_creationStarvedByHiddenEra) return;`)");

        // The consume and the stale-measure drop must sit AFTER the gate:
        // a consume hoisted above it reads the flag before the gate does
        // and the flip body never runs for the pane that needs it.
        var consume = Assert.Single(watch.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "false");
        Assert.True(flipGate.Span.End < consume.SpanStart,
            "the starved flag is consumed after the flip gate, never before "
                + "it: a consume above the gate turns the gate off for the "
                + "very pane it exists for");
    }

    /// <summary>
    /// The zero-handle refusal: the re-push hands the surface pointer to
    /// the native side twice (scale, then size), and a surface whose
    /// handle reads IntPtr.Zero is a surface the renderer lost or the
    /// teardown already freed - re-pushing onto it dereferences a dead
    /// pointer native side. The refusal must exist in the watch, must
    /// TERMINATE, and must precede BOTH pushes; deleting it leaves every
    /// other fact in this file green while the app dies behind it.
    /// </summary>
    [Fact]
    public void ShowWatch_RefusesToRePushOntoAZeroedSurface()
    {
        var watch = Terminal().Method("OnLayoutShowWatch");

        // Non-vacuity: both halves of the pair are still pushed here.
        var scale = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetContentScale");
        var size = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetSize");

        var zeroGate = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>(), RefusesZeroHandle);
        Assert.True(zeroGate.Statement is ReturnStatementSyntax
                    || (zeroGate.Statement is BlockSyntax zb
                        && zb.Statements.OfType<ReturnStatementSyntax>().Any()),
            "the zero-handle refusal must RETURN: a check that falls "
                + "through re-pushes onto the freed surface anyway");
        Assert.True(zeroGate.SpanStart < scale.SpanStart
                    && zeroGate.SpanStart < size.SpanStart,
            "the zero-handle refusal must precede BOTH pushes of the pair: "
                + "a scale or size push onto a zeroed handle is a native "
                + "dereference of a surface that no longer exists");
    }

    /// <summary>
    /// The flip settles a still-pending creation at the first visible
    /// pass. The settle attempt must sit INSIDE the still-pending branch
    /// (`if (!_surfaceCreated)`): containment, not order -- an attempt
    /// moved below the emptied branch either never runs for a pending
    /// pane (the pane starves forever) or runs for a created one (the
    /// surface is created at twice). The consume and the stale-measure
    /// drop must both precede the attempt inside the same method.
    /// </summary>
    [Fact]
    public void ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass()
    {
        var watch = Terminal().Method("OnLayoutShowWatch");

        // Non-vacuity: the settle attempt and the hidden-pass arming are
        // both still here.
        var settle = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TrySettleSurfaceCreation");
        var arming = Assert.Single(watch.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "true");

        var guard = GuardBefore(watch, "TrySettleSurfaceCreation");
        Assert.True(guard is not null,
            "the flip must refuse to act on an invisible pass: an "
                + "invisible pane's arrange is still the placeholder");
        AssertGuardReturns(guard!, "OnLayoutShowWatch's invisible-pass guard");

        // Containment: the third producer of the starved flag - the
        // watch's own invisible branch - records INSIDE the guard and
        // before its return, the same shape the other two producers are
        // pinned with. Hoisted above the guard it arms the backstop on a
        // visible pass too, and every visible pane re-runs the flip body.
        Assert.True(guard.Contains(arming),
            "the invisible-pass arming must sit INSIDE the invisible-pass "
                + "guard's branch: hoisted above the guard it arms the "
                + "backstop on a visible pass too, and every visible pane "
                + "re-runs the flip body");
        var armingReturn = guard.Statement is ReturnStatementSyntax ar
            ? ar
            : guard.Statement is BlockSyntax ab
                ? ab.Statements.OfType<ReturnStatementSyntax>().First()
                : null;
        Assert.True(armingReturn is not null && arming.SpanStart < armingReturn.SpanStart,
            "the invisible-pass arming must precede the branch's return: a "
                + "record placed after an unconditional return never runs");

        // Containment: the attempt belongs to the still-pending branch's
        // OWN statement, not merely somewhere inside the if's full span -
        // an else clause is part of that span, and a settle moved into it
        // runs only for panes that already have a surface.
        var pendingBranch = watch.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Contains("_surfaceCreated"));
        Assert.True(pendingBranch is not null,
            "the watch needs a `!_surfaceCreated` branch that owns the "
                + "settle attempt");
        Assert.True(pendingBranch!.Statement.Contains(settle),
            "the settle attempt must sit INSIDE the `if (!_surfaceCreated)` "
                + "branch's own statement: moved out of the branch - below "
                + "it, or into its else - a pending pane never settles (the "
                + "show-path backstop is dead) and a created pane creates "
                + "its surface a second time");

        var consume = Assert.Single(watch.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "false");
        Assert.True(consume.SpanStart < settle.SpanStart,
            "the starved flag must be consumed before the attempt: a flag "
                + "left set makes every later visible pass re-run the flip");

        var rebase = Assert.Single(watch.Body!.AssignsTo("_hasCreationMeasure"),
            a => a.Right.ToString() == "false");
        Assert.True(rebase.SpanStart < settle.SpanStart,
            "a measure settled on hidden passes is stale by construction: "
                + "it must be dropped before the attempt, or the two-pass "
                + "settle gate compares against a placeholder");
    }

    /// <summary>
    /// For a surface that already exists, the flip re-pushes the
    /// size/scale pair TOGETHER. While hidden, neither half is pushed --
    /// PushSurfaceSize refuses the size, OnCompositionScaleChanged refuses
    /// the scale -- because a scale pushed alone re-derives the grid from
    /// a size the pane is not showing. The show pass is the one place
    /// both halves can be safely re-read, so it pushes both, and both
    /// pushes belong to the existing-surface path: neither may sit in the
    /// still-pending branch, where no surface exists to receive them.
    /// </summary>
    [Fact]
    public void ShowWatch_RePushesTheScaleAndSizePairTogether()
    {
        var watch = Terminal().Method("OnLayoutShowWatch");

        // Non-vacuity: both halves of the pair are still pushed here.
        var scale = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetContentScale");
        var size = Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetSize");

        var guard = GuardBefore(watch, "SurfaceSetContentScale");
        Assert.True(guard is not null && GuardBefore(watch, "SurfaceSetSize") is not null,
            "the re-push must sit behind the visibility guard like every "
                + "other push");
        AssertGuardReturns(guard!,
            "OnLayoutShowWatch's invisible-pass guard (it is the re-push's guard too)");

        // Containment: the pair is the existing-surface path's, not the
        // pending branch's. An invisible pass must not reach either half.
        var pendingBranch = watch.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Contains("_surfaceCreated"));
        Assert.True(pendingBranch is not null
                        && !pendingBranch!.Contains(scale)
                        && !pendingBranch.Contains(size),
            "the size/scale re-push belongs after the still-pending branch: "
                + "a push inside it acts on a surface that does not exist");

        Assert.True(scale.SpanStart < size.SpanStart,
            "scale then size, the order every creation uses: the grid is "
                + "derived from pixels, and the pixels are derived at the "
                + "new scale");
        Assert.True(SpanOf(watch, "TrySettleSurfaceCreation") < scale.SpanStart,
            "the re-push belongs to the existing-surface branch, after the "
                + "pending-creation settle returns");
    }

    /// <summary>
    /// OnCompositionScaleChanged refuses while hidden, for the same
    /// reason its companion size push does: a scale pushed alone
    /// re-derives the grid from a size the pane is not showing, and
    /// nothing on the hidden path re-reads it. The show pass re-pushes
    /// the pair; a scale change that landed while hidden must not have
    /// been half-applied at show time.
    /// </summary>
    [Fact]
    public void ScaleChange_RefusesWhileHidden()
    {
        var scale = Terminal().Method("OnCompositionScaleChanged");

        // Non-vacuity: the scale push itself is still here.
        Assert.Single(scale.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetContentScale");

        var guard = GuardBefore(scale, "SurfaceSetContentScale");
        Assert.True(guard is not null,
            "OnCompositionScaleChanged needs an `!IsEffectivelyVisible()` "
                + "return before the scale push: the pair moves together "
                + "or not at all, and the show pass is what re-pushes both");
        AssertGuardReturns(guard!, "OnCompositionScaleChanged's hidden refusal");
    }

    /// <summary>
    /// The 250 ms Loaded fallback is one-shot, so a pane whose tick lands
    /// while hidden has spent its fallback on nothing. It must RECORD the
    /// era for the show-path backstop on its way out, and the record must
    /// live INSIDE the hidden branch that returns: containment, not
    /// order. A record hoisted above the guard arms the backstop on a
    /// visible tick too; a hidden branch that records but falls through
    /// settles at the placeholder anyway.
    /// </summary>
    [Fact]
    public void LoadedFallback_RecordsTheHiddenEraInsteadOfSettling()
    {
        var loaded = Terminal().Method("OnLoaded");

        // Non-vacuity: the visible path of this fallback still settles.
        Assert.Single(loaded.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TrySettleSurfaceCreation");

        var guard = GuardBefore(loaded, "_creationStarvedByHiddenEra");
        Assert.True(guard is not null,
            "the fallback's hidden branch needs the visibility refusal "
                + "around its starvation record: a hidden tick must record "
                + "and return, never settle at the placeholder");
        AssertGuardReturns(guard!, "the Loaded fallback's hidden branch");

        var record = Assert.Single(loaded.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "true");
        Assert.True(guard!.Contains(record),
            "the starvation record must sit INSIDE the fallback's hidden "
                + "branch: hoisted above the guard it arms the backstop on "
                + "a visible tick too, and every visible pass re-runs the "
                + "flip body");
        Assert.True(record.SpanStart < SpanOf(loaded, "TrySettleSurfaceCreation"),
            "the starvation record must precede the settle attempt: on a "
                + "hidden tick the record runs and the return in its guard "
                + "skips the attempt");
    }

    /// <summary>
    /// The funnel, counted over EVERY shell source, for both halves of
    /// the pair: SurfaceSetSize is pushed from exactly three sites (the
    /// creation handler's settled tail, the show-path re-push, and
    /// PushSurfaceSize), SurfaceSetContentScale from exactly three (the
    /// same tail, the same re-push, and OnCompositionScaleChanged), every
    /// one behind a terminating effective-visibility refusal, and
    /// TryCreateSurface has exactly one caller (TrySettleSurfaceCreation).
    ///
    /// The census matches on the callee's TAIL over the whole embedded
    /// corpus, so the receiver's spelling cannot hide a site: a using
    /// alias (`using NM = ...; NM.SurfaceSetSize(...)`), a using-static
    /// bare call, or a brand-new driver in another file each add a site
    /// the expected set does not carry. A fourth push site, an unguarded
    /// one, or a second creation caller is a new path around the guards
    /// this class pins.
    ///
    /// One blind spot the parse itself creates: the corpus is parsed with
    /// DEMO, DEBUG and TESTSEAM defined, so regions disabled under those
    /// symbols are invisible to the call census. The disabled-text check
    /// below refuses any push callee found in one, which turns "add a
    /// push behind an #if" into a decision this fact participates in
    /// rather than a silent escape.
    /// </summary>
    [Fact]
    public void CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource()
    {
        var expectedSize = new HashSet<string>(StringComparer.Ordinal)
        {
            "Controls.TerminalControl.xaml.cs::OnFirstLayoutUpdated",
            "Controls.TerminalControl.xaml.cs::OnLayoutShowWatch",
            "Controls.TerminalControl.xaml.cs::PushSurfaceSize",
        };
        var expectedScale = new HashSet<string>(StringComparer.Ordinal)
        {
            "Controls.TerminalControl.xaml.cs::OnFirstLayoutUpdated",
            "Controls.TerminalControl.xaml.cs::OnLayoutShowWatch",
            "Controls.TerminalControl.xaml.cs::OnCompositionScaleChanged",
        };

        var seenSize = new HashSet<string>(StringComparer.Ordinal);
        var seenScale = new HashSet<string>(StringComparer.Ordinal);
        var creators = new List<(string File, InvocationExpressionSyntax Call)>();

        var files = ShellSource.AllFiles();
        foreach (var file in files)
        {
            foreach (var call in file.Root.CallsEndingWith("SurfaceSetSize"))
            {
                var site = CensusSite(file, call);
                Assert.True(expectedSize.Contains(site),
                    $"unexpected SurfaceSetSize call site '{site}' (callee matched by " +
                        "tail, aliases included): every pane-size push must sit in a " +
                        "guarded TerminalControl method");
                seenSize.Add(site);
            }

            foreach (var call in file.Root.CallsEndingWith("SurfaceSetContentScale"))
            {
                var site = CensusSite(file, call);
                Assert.True(expectedScale.Contains(site),
                    $"unexpected SurfaceSetContentScale call site '{site}' (callee " +
                        "matched by tail, aliases included): every pane-scale push " +
                        "must sit in a guarded TerminalControl method");
                seenScale.Add(site);
            }

            foreach (var call in file.Root.CallsEndingWith("TryCreateSurface"))
            {
                creators.Add((file.Name, call));
            }
        }

        // The parse defines DEMO, DEBUG and TESTSEAM, so a region disabled
        // under those symbols is invisible to the call census above: a
        // push compiled only in a configuration this parse cannot see
        // (inside `#if !DEBUG`, say) would slip it while shipping in the
        // configurations that matter. Disabled regions survive as trivia,
        // so refuse any push-shaped callee that appears in one: the push
        // must live in a visible region, or this census has to be taught
        // the symbol deliberately, out loud.
        foreach (var file in files)
        {
            foreach (var disabled in file.Root.DescendantTrivia()
                         .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                var text = disabled.ToString();
                Assert.False(text.Contains("SurfaceSetSize(", StringComparison.Ordinal),
                    $"{file.Name} disables a preprocessor region containing a " +
                        "'SurfaceSetSize(' call: the census cannot see it, but a " +
                        "build without this parse's symbols compiles it");
                Assert.False(text.Contains("SurfaceSetContentScale(", StringComparison.Ordinal),
                    $"{file.Name} disables a preprocessor region containing a " +
                        "'SurfaceSetContentScale(' call: the census cannot see it, " +
                        "but a build without this parse's symbols compiles it");
            }
        }

        // Both halves, guarded SITE BY CALLEE: the two methods that host
        // both halves are dual-listed, so a site's guard must be proven
        // for each callee it pushes - a scale hoisted above the guard
        // survives a check that only asks where the size push sits.
        foreach (var (site, callee) in seenSize
                     .Select(s => (s, "SurfaceSetSize"))
                     .Concat(seenScale.Select(s => (s, "SurfaceSetContentScale"))))
        {
            var (tail, method) = site.Split("::", 2) is { Length: 2 } parts
                ? (parts[0], parts[1])
                : throw new InvalidOperationException("unparseable census site " + site);
            var source = ShellSource.Load(tail);
            var declared = source.Method(method);
            var guard = GuardBefore(declared, callee);
            Assert.True(guard is not null,
                $"{site} pushes {callee} without an `!IsEffectivelyVisible()` " +
                    "refusal before it");
            AssertGuardReturns(guard!, site + "::" + callee);
        }

        // Exhaustiveness the other way: a vanished site is a lost push
        // path, not a smaller funnel to maintain.
        Assert.True(expectedSize.SetEquals(seenSize),
            "SurfaceSetSize sites missing from the shell: "
                + string.Join(", ", expectedSize.Except(seenSize)));
        Assert.True(expectedScale.SetEquals(seenScale),
            "SurfaceSetContentScale sites missing from the shell: "
                + string.Join(", ", expectedScale.Except(seenScale)));

        var creator = Assert.Single(creators);
        Assert.Equal("Controls.TerminalControl.xaml.cs", creator.File);
        Assert.Equal("TrySettleSurfaceCreation",
            creator.Call.Ancestors().OfType<MethodDeclarationSyntax>().First()
                .Identifier.ValueText);
    }

    /// <summary>
    /// A census site's identity: the embedded source's tail name and the
    /// method the call sits in. A call outside any method (a field
    /// initializer, top-level code) is not a site the census can vouch
    /// for and fails the expected-set check by construction.
    /// </summary>
    private static string CensusSite(ShellSource file, InvocationExpressionSyntax call)
    {
        var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return file.Name + "::" + (method?.Identifier.ValueText ?? "<no method>");
    }

    private static ShellSource MainWindowSource() =>
        ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// The host shape that makes `IsEffectivelyVisible` the discriminator
    /// this file's guards use: MainWindow parents every tab's PaneHost
    /// COLLAPSED and flips only the active one visible, so every
    /// non-active pane is effectively invisible from the moment it enters
    /// the tree. This pins PRE-EXISTING MainWindow shape, not a change
    /// this fix made: it is the runtime fact the guards' predicate rides,
    /// and a host that stops collapsing inactive panes (or stops flipping
    /// the active one) would reopen the hidden-era class with every guard
    /// above still green. The fact is red against a host mutant: removing
    /// the collapse or the flip fails the asserts below.
    ///
    /// Note the era this shape produces: a restored background tab enters
    /// the tree collapsed and stays there (the restore seeds and adopts
    /// its tabs before the TabAdded subscription exists, and one
    /// SwapActivePane flips only the saved-active tab visible), so it
    /// never arranges at all until it is first shown. The stale positive
    /// read the creation gate refuses comes from the OTHER shape: a pane
    /// arranged while visible and collapsed again afterwards keeps that
    /// arrange as its read.
    /// </summary>
    [Fact]
    public void TabHost_CollapsesInactiveHostsAndFlipsTheActiveOne()
    {
        var collapse = MainWindowSource().Method("AddPaneHost");
        Assert.Contains(collapse.Body!.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "paneHost.Visibility"
                 && a.Right.ToString() == "Visibility.Collapsed");

        var flip = MainWindowSource().Method("SwapActivePane");
        Assert.Contains(flip.Body!.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "host.Visibility"
                 && a.Right.ToString().Contains("Visibility.Visible")
                 && a.Right.ToString().Contains("Visibility.Collapsed"));
    }
}
