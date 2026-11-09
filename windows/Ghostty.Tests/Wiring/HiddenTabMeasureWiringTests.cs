using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A hidden tab's placeholder arrange is not a measurement. The tab host
/// realizes content behind the active tab and sizes it to a default slot
/// (800x600 in the birth-coverage record), so a hidden pane's panel reads
/// a nonzero size that is not the pane's eventual one -- and every
/// creation driver that trusts the panel's current read (the first-layout
/// settle gate, the Loaded fallback, the retry tick) then births the pty
/// at that placeholder grid: the shell formats its startup output for a
/// size the pane never shows, and the #1159 equality reds on a tab that
/// was born behind another one (`pty 79x28 (800x600 px), pane 255x46`).
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

        // Non-vacuity: the measured gate itself is still here.
        var measuredGate = Assert.Single(create.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>(),
            s => s.Condition.ToString().Contains("SurfacePixelSize.Initial"));

        Assert.Contains("!IsEffectivelyVisible()",
            measuredGate.Condition.ToString());
        Assert.True(measuredGate.Condition is BinaryExpressionSyntax,
            "the visibility refusal must sit IN the measured gate's condition "
                + "(`|| !IsEffectivelyVisible()`), not in a statement of its own");
    }

    /// <summary>
    /// A layout pass while the pane is hidden must count for nothing in
    /// the first-layout handler: not toward the settle gate (a repeated
    /// placeholder is not a settled size), not as the size push, and not
    /// as the handler's one retirement. The guard sits before all three,
    /// so the pane's first visible pass is the one that does the work.
    /// </summary>
    [Fact]
    public void FirstLayoutHandler_IgnoresPassesWhileHidden()
    {
        var handler = Terminal().Method("OnFirstLayoutUpdated");

        // Non-vacuity: the work the guard must precede is still here.
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
        Assert.True(guard!.SpanStart < SpanOf(handler, "LayoutUpdated"),
            "the hidden-pass guard must also precede the handler's "
                + "retirement, or a placeholder pass retires the one-shot "
                + "and the pane's first visible pass goes unheard");
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
    /// The flip settles a still-pending creation at the first visible
    /// pass, in the order that makes it safe: the visible pass must pass
    /// the visibility guard, the starved flag must be consumed (and a
    /// measure settled on hidden passes dropped as stale) before the
    /// attempt, and the attempt must sit in the still-pending branch --
    /// a pane whose surface already exists takes the re-push below
    /// instead of a second creation attempt.
    /// </summary>
    [Fact]
    public void ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass()
    {
        var watch = Terminal().Method("OnLayoutShowWatch");

        // Non-vacuity: the settle attempt and the hidden-pass arming are
        // both still here.
        Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TrySettleSurfaceCreation");
        Assert.Single(watch.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "true");

        var guard = GuardBefore(watch, "TrySettleSurfaceCreation");
        Assert.True(guard is not null,
            "the flip must refuse to act on an invisible pass: an "
                + "invisible pane's arrange is still the placeholder");

        var settle = SpanOf(watch, "TrySettleSurfaceCreation");
        var consume = Assert.Single(watch.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "false");
        Assert.True(consume.SpanStart < settle,
            "the starved flag must be consumed before the attempt: a flag "
                + "left set makes every later visible pass re-run the flip");

        var rebase = Assert.Single(watch.Body!.AssignsTo("_hasCreationMeasure"),
            a => a.Right.ToString() == "false");
        Assert.True(rebase.SpanStart < settle,
            "a measure settled on hidden passes is stale by construction: "
                + "it must be dropped before the attempt, or the two-pass "
                + "settle gate compares against a placeholder");

        var pendingBranch = watch.Body!.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Contains("_surfaceCreated"));
        Assert.True(pendingBranch is not null
                        && pendingBranch.SpanStart < settle,
            "the settle attempt must sit in the still-pending branch "
                + "(`if (!_surfaceCreated)`): an existing surface must not "
                + "be created at again");
    }

    /// <summary>
    /// For a surface that already exists, the flip re-pushes the
    /// size/scale pair TOGETHER. While hidden, neither half is pushed --
    /// PushSurfaceSize refuses the size, OnCompositionScaleChanged refuses
    /// the scale -- because a scale pushed alone re-derives the grid from
    /// a size the pane is not showing. The show pass is the one place
    /// both halves can be safely re-read, so it pushes both, after the
    /// pending-creation branch.
    /// </summary>
    [Fact]
    public void ShowWatch_RePushesTheScaleAndSizePairTogether()
    {
        var watch = Terminal().Method("OnLayoutShowWatch");

        // Non-vacuity: both halves of the pair are still pushed here.
        Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetContentScale");
        Assert.Single(watch.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "NativeMethods.SurfaceSetSize");

        Assert.True(GuardBefore(watch, "SurfaceSetContentScale") is not null
                        && GuardBefore(watch, "SurfaceSetSize") is not null,
            "the re-push must sit behind the visibility guard like every "
                + "other push");

        Assert.True(SpanOf(watch, "SurfaceSetContentScale")
                        < SpanOf(watch, "SurfaceSetSize"),
            "scale then size, the order every creation uses: the grid is "
                + "derived from pixels, and the pixels are derived at the "
                + "new scale");
        Assert.True(SpanOf(watch, "TrySettleSurfaceCreation")
                        < SpanOf(watch, "SurfaceSetContentScale"),
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
    }

    /// <summary>
    /// The 250 ms Loaded fallback is one-shot, so a pane whose tick lands
    /// while hidden has spent its fallback on nothing. It must RECORD the
    /// era for the show-path backstop on its way out -- the record, not a
    /// settle attempt, is what the hidden branch does; the visible branch
    /// below it still settles.
    /// </summary>
    [Fact]
    public void LoadedFallback_RecordsTheHiddenEraInsteadOfSettling()
    {
        var loaded = Terminal().Method("OnLoaded");

        // Non-vacuity: the visible path of this fallback still settles.
        Assert.Single(loaded.Body!.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TrySettleSurfaceCreation");

        Assert.True(GuardBefore(loaded, "_creationStarvedByHiddenEra") is not null,
            "the fallback's hidden branch needs the visibility refusal "
                + "around its starvation record: a hidden tick must record "
                + "and return, never settle at the placeholder");

        var record = Assert.Single(loaded.Body!.AssignsTo("_creationStarvedByHiddenEra"),
            a => a.Right.ToString() == "true");
        Assert.True(record.SpanStart < SpanOf(loaded, "TrySettleSurfaceCreation"),
            "the starvation record must precede the settle attempt: on a "
                + "hidden tick the record runs and the return in its guard "
                + "skips the attempt");
    }

    /// <summary>
    /// The funnel, counted: SurfaceSetSize is pushed from exactly three
    /// sites (the creation handler's settled tail, the show-path
    /// re-push, and PushSurfaceSize), every one behind an effective-
    /// visibility refusal, and TryCreateSurface has exactly one caller
    /// (TrySettleSurfaceCreation). A fourth push site or a second
    /// creation caller is a new path around the guards this class pins.
    /// </summary>
    [Fact]
    public void CreationFunnel_SizePushSitesAreGuardedAndCreationHasOneCaller()
    {
        var terminal = Terminal();

        var sizePushes = terminal.Root.Calls("NativeMethods.SurfaceSetSize");
        var guardedMethods = new[]
            { "OnFirstLayoutUpdated", "OnLayoutShowWatch", "PushSurfaceSize" };
        Assert.True(sizePushes.Count == guardedMethods.Length,
            $"expected exactly {guardedMethods.Length} SurfaceSetSize call sites "
                + $"({string.Join(", ", guardedMethods)}), found {sizePushes.Count}");

        foreach (var push in sizePushes)
        {
            var method = push.Ancestors().OfType<MethodDeclarationSyntax>().First();
            Assert.Contains(method.Identifier.ValueText, guardedMethods);
            Assert.True(GuardBefore(method, "SurfaceSetSize") is not null,
                $"{method.Identifier.ValueText} pushes SurfaceSetSize without an "
                    + "`!IsEffectivelyVisible()` refusal before it");
        }

        var creator = Assert.Single(terminal.Root.Calls("TryCreateSurface"));
        Assert.Equal("TrySettleSurfaceCreation",
            creator.Ancestors().OfType<MethodDeclarationSyntax>().First()
                .Identifier.ValueText);
    }

    private static ShellSource MainWindowSource() =>
        ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// The hidden era this file guards has a concrete producer: the tab
    /// host. MainWindow parents every tab's PaneHost COLLAPSED and flips
    /// only the active one visible, so during session restore each
    /// restored tab passes through visible at whatever size the window
    /// then has (a fresh window's default client slot before its
    /// persisted size applies), is arranged there, and keeps that
    /// arrange as its positive read once collapsed. Both sites must
    /// keep their shape: the collapse is what makes a hidden pane read a
    /// placeholder, and the flip is what the show-path backstop rides.
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
