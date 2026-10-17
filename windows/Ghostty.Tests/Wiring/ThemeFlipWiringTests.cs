using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The forced theme flips that make MUXC re-read overridden tab-strip
/// resources run on the dispatcher, never in the caller's frame.
///
/// A click in the new-tab flyout used to walk straight into one of these
/// flips: the handler wrote RequestedTheme synchronously, MUXC walked the
/// subtree re-resolving theme resources, and the walk dereferenced a
/// reference MUXC had already freed -- a hard crash in the framework layer
/// with our write at the top of the stack. The flip itself is legitimate;
/// running it inside input delivery is not. These guards pin the three
/// halves of that fix: the flip writes live inside an enqueued callback,
/// a flip with nothing new to re-read is skipped, and every resource write
/// still marks the strip dirty so a skipped flip never skips a re-read the
/// next one owes.
///
/// Wiring guards: read as syntax shape because the shell assembly cannot
/// load in a test host. Written to fail on the mutations that matter (a
/// flip moved back inline, a guard deleted, a dirty flag dropped) rather
/// than on the text changing.
/// </summary>
public class ThemeFlipWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Tabs.TabHost.xaml.cs");
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    /// <summary>
    /// The one enqueued callback a flip method contains, with the enqueue
    /// invocation asserted to name the Low priority: a flip at Normal rides
    /// ahead of layout work the deferral exists to let finish.
    /// </summary>
    private static (LambdaExpressionSyntax Lambda, InvocationExpressionSyntax Enqueue)
        SingleEnqueue(MethodDeclarationSyntax method)
    {
        var enqueues = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax m
                && m.Name.Identifier.ValueText == "TryEnqueue")
            .ToList();
        Assert.True(enqueues.Count == 1,
            $"expected exactly one DispatcherQueue.TryEnqueue in {method.Identifier.ValueText}, found {enqueues.Count}");
        var enqueue = enqueues[0];
        Assert.Contains("Low", enqueue.ToString());
        var lambda = enqueue.ArgumentList.Arguments.Last().Expression as LambdaExpressionSyntax;
        Assert.NotNull(lambda);
        return (lambda!, enqueue);
    }

    private static System.Collections.Generic.IEnumerable<LambdaExpressionSyntax>
        EnqueuedCallbacks(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax m
                && m.Name.Identifier.ValueText == "TryEnqueue")
            .Select(i => i.ArgumentList.Arguments.Last().Expression)
            .OfType<LambdaExpressionSyntax>();

    [Fact]
    public void RefreshNavViewTheme_TheFlipIsEnqueuedNotInline()
    {
        // The crash site: this flip walked into freed theme resources from
        // inside a pointer-up handler. Both of its RequestedTheme writes
        // must sit in the dispatcher callback, and the callback is coalesced
        // -- the queued flag keeps a burst of callers from stacking enqueues
        // that each walk the tree.
        var method = Strip().Method("RefreshNavViewTheme");
        var (lambda, _) = SingleEnqueue(method);

        var writes = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "NavView.RequestedTheme")
            .ToList();
        Assert.True(writes.Count == 2,
            $"expected the toggle pair, found {writes.Count} NavView.RequestedTheme writes");
        foreach (var write in writes)
            Assert.True(lambda.Span.Contains(write.Span),
                "a NavView.RequestedTheme write sits outside the enqueued callback");

        var queuedGuard = method.Body!.Statements.OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("_navRereadQueued"));
        Assert.NotNull(queuedGuard);
        Assert.Contains("return", queuedGuard!.Statement.ToString());
    }

    [Fact]
    public void RefreshNavViewTheme_SkipsAFlipWithNothingToReread()
    {
        // The flip exists to re-read resource overrides; with none written
        // since the last flip it is a full theme walk for nothing, on every
        // tab open. The dirty flag is the memo.
        var method = Strip().Method("RefreshNavViewTheme");
        var guard = method.Body!.Statements.OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("_navResourcesDirty"));
        Assert.NotNull(guard);
        Assert.Contains("return", guard!.Statement.ToString());
    }

    [Fact]
    public void TheStrip_MarksItsNavResourcesDirtyOnEveryWrite()
    {
        // The memo is only honest if every write sets it. All NavView
        // resource writes go through the two helpers, and the helpers are
        // the only places allowed to touch NavView.Resources: a direct
        // write elsewhere flips nothing and reads stale.
        foreach (var name in new[] { "SetNavResource", "ClearNavResource" })
        {
            var helper = Strip().Method(name);
            var marks = helper.AssignsTo("_navResourcesDirty").ToList();
            Assert.True(marks.Count == 1,
                $"{name} must mark the strip dirty exactly once, found {marks.Count}");
            Assert.Equal("true", marks[0].Right.ToString());
        }

        var writes = Strip().Root.DescendantNodes()
            .Where(n =>
                (n is AssignmentExpressionSyntax a && a.Left.ToString().Contains("NavView.Resources")) ||
                (n is InvocationExpressionSyntax i && i.Expression.ToString() == "NavView.Resources.Remove"))
            .ToList();
        Assert.NotEmpty(writes);
        foreach (var write in writes)
        {
            var enclosing = write.Ancestors().OfType<MethodDeclarationSyntax>()
                .First().Identifier.ValueText;
            Assert.True(enclosing is "SetNavResource" or "ClearNavResource",
                $"a NavView.Resources write lives in {enclosing}, outside the two helpers");
        }
    }

    [Fact]
    public void RefreshNavViewTheme_UnlatchesTheQueuedFlagWhenEnqueueRefuses()
    {
        // TryEnqueue answers false when the dispatcher queue is shutting
        // down, and the callback then never runs. A queued flag left
        // latched by that refusal would swallow every later re-read for
        // the life of the control, so the refusal path must put it back.
        var method = Strip().Method("RefreshNavViewTheme");
        var (lambda, _) = SingleEnqueue(method);

        var unlatch = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_navRereadQueued"
                && a.Right.ToString() == "false"
                && !lambda.Span.Contains(a.Span))
            .ToList();
        Assert.Single(unlatch);
        var guard = unlatch[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(guard);
        Assert.True(
            guard!.Condition.ToString()
                .Contains("enqueue", System.StringComparison.OrdinalIgnoreCase),
            "the queued flag is reset outside a branch that tests the enqueue result");
    }

    [Fact]
    public void RefreshTabViewTheme_UnlatchesTheQueuedFlagWhenEnqueueRefuses()
    {
        // The horizontal host's copy of the same refusal rule.
        var method = Host().Method("RefreshTabViewTheme");
        var (lambda, _) = SingleEnqueue(method);

        var unlatch = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_tabViewRereadQueued"
                && a.Right.ToString() == "false"
                && !lambda.Span.Contains(a.Span))
            .ToList();
        Assert.Single(unlatch);
        var guard = unlatch[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(guard);
        Assert.True(
            guard!.Condition.ToString()
                .Contains("enqueue", System.StringComparison.OrdinalIgnoreCase),
            "the queued flag is reset outside a branch that tests the enqueue result");
    }

    [Fact]
    public void RefreshTabViewTheme_TheFlipIsEnqueuedAndSkipsWhenIdle()
    {
        // The horizontal host's copy of the same flip, with the same two
        // halves: enqueued, coalesced, and skipped when no resource write
        // is pending.
        var method = Host().Method("RefreshTabViewTheme");
        var (lambda, _) = SingleEnqueue(method);

        var writes = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "TabViewControl.RequestedTheme")
            .ToList();
        Assert.True(writes.Count == 2,
            $"expected the toggle pair, found {writes.Count} TabViewControl.RequestedTheme writes");
        foreach (var write in writes)
            Assert.True(lambda.Span.Contains(write.Span),
                "a TabViewControl.RequestedTheme write sits outside the enqueued callback");

        var dirtyGuard = method.Body!.Statements.OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("_tabViewResourcesDirty"));
        Assert.NotNull(dirtyGuard);
        Assert.Contains("return", dirtyGuard!.Statement.ToString());

        var queuedGuard = method.Body!.Statements.OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("_tabViewRereadQueued"));
        Assert.NotNull(queuedGuard);
        Assert.Contains("return", queuedGuard!.Statement.ToString());
    }

    [Fact]
    public void TabHost_TheFlipIsNeverInlinedAtACallSite()
    {
        // Three sites used to inline the Light-and-back pair after writing
        // a resource. They must route through the guarded re-read instead:
        // an inlined flip is a synchronous theme walk in whatever frame the
        // caller runs in, which is the crash shape.
        foreach (var name in new[] { "ApplyShellTheme", "ClearShellTheme", "SetChromeFill" })
        {
            var site = Host().Method(name);
            Assert.Empty(site.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == "TabViewControl.RequestedTheme"));
            Assert.Contains("RefreshTabViewTheme()", site.ToString());
        }
    }

    [Fact]
    public void TabHost_EveryTabViewThemeWriteIsDeferredOrAPlainSet()
    {
        // File-wide: the only TabViewControl.RequestedTheme writes allowed
        // outside an enqueued callback are the plain single sets in
        // SetRequestedTheme, where the element's own theme is decided. A
        // write pair anywhere else is a forced flip back on a synchronous
        // path.
        var root = Host().Root;
        var callbacks = EnqueuedCallbacks(root).ToList();
        var writes = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "TabViewControl.RequestedTheme")
            .ToList();
        Assert.NotEmpty(writes);
        foreach (var write in writes)
        {
            var deferred = callbacks.Any(c => c.Span.Contains(write.Span));
            var enclosing = write.Ancestors().OfType<MethodDeclarationSyntax>()
                .First().Identifier.ValueText;
            Assert.True(deferred || enclosing == "SetRequestedTheme",
                $"TabViewControl.RequestedTheme is written synchronously in {enclosing}");
        }
    }

    [Fact]
    public void Strip_EveryNavViewThemeWriteIsDeferred()
    {
        // File-wide, with no plain-set exception: nothing but the flip
        // itself may ever write NavView.RequestedTheme.
        var root = Strip().Root;
        var callbacks = EnqueuedCallbacks(root).ToList();
        var writes = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "NavView.RequestedTheme")
            .ToList();
        Assert.NotEmpty(writes);
        foreach (var write in writes)
            Assert.True(callbacks.Any(c => c.Span.Contains(write.Span)),
                "NavView.RequestedTheme is written outside an enqueued callback");
    }

    [Fact]
    public void TabHost_EveryTabResourceWriteMarksItDirty()
    {
        // The horizontal host writes TabView resources from several
        // methods; every one of them owes the memo a dirty mark, or a
        // later flip skips a re-read a caller was owed.
        var root = Host().Root;
        var writers = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString().Contains("TabViewControl.Resources")) ||
                m.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(i => i.Expression.ToString() == "TabViewControl.Resources.Remove"))
            .ToList();
        Assert.NotEmpty(writers);
        foreach (var writer in writers)
        {
            var marks = writer.AssignsTo("_tabViewResourcesDirty").ToList();
            Assert.True(marks.Count >= 1,
                $"{writer.Identifier.ValueText} writes TabView resources without marking them dirty");
        }
    }

    // ------------------------------------------------------------------
    // The resolved-theme landing. A theme preview applies its palette
    // synchronously, but WindowThemeManager resolves the light/dark answer
    // (window-theme unset means "auto", which reads the background's
    // luminance) on a LATER dispatcher turn, and only announces it when it
    // actually flipped. The horizontal strip's unselected titles are a
    // cached brush scored against a ground estimate that blends that
    // polarity, and nothing re-pushes the ground when the flip lands: the
    // titles kept the ink picked for the outgoing polarity on a strip whose
    // surface ApplyTheme had just darkened. A second dark theme re-derived
    // the ink through the synchronous config pass, which is why going to
    // another dark theme and back fixed it. The vertical strip never saw
    // it: its unselected rows follow the element theme's own resources, so
    // the same flip re-resolves them for free.
    // ------------------------------------------------------------------

    private static ShellSource Window() => ShellSource.Load("MainWindow.xaml.cs");
    private static ShellSource Manager() => ShellSource.Load("Services.WindowThemeManager.cs");

    [Fact]
    public void OnWindowThemeChanged_TheLandedPolarityRepaintsTheGround()
    {
        // The landing handler is the only place the deferred flip arrives.
        // Both pushes are pinned through the shared shape checker: exactly
        // one call each, a direct top-level statement of the handler's own
        // body, with no return anywhere above it.
        var handler = Window().Method("OnWindowThemeChanged");
        foreach (var callee in new[] { "ApplyTheme", "RefreshBackdropChrome" })
            CallShape.TopLevelCall(handler, callee);
    }

    [Fact]
    public void RefreshBackdropChrome_StillReachesTheStripsGroundPush()
    {
        // The chain the repaint above rides. Breaking any link silently
        // turns the handler's push into a no-op.
        var window = Window();
        Assert.NotEmpty(
            window.Method("RefreshBackdropChrome").Calls("ApplyVerticalTitleBarChrome"));
        Assert.NotEmpty(
            window.Method("ApplyVerticalTitleBarChrome").Calls("ApplyChromeSeparators"));
        Assert.NotEmpty(
            window.Method("ApplyChromeSeparators").CallsEndingWith("SetChromeGround"));
    }

    [Fact]
    public void SetChromeGround_TheInkPassesRunOnEveryPush()
    {
        // The invariant: the ink passes are unconditional and the ground's
        // landed value is written before they run, top-level and ungated.
        // The polarity behind the estimate can flip without moving the
        // value (a tint-opacity near 1.0 rounds the base's contribution
        // away, so a light->dark pair with the same background lands
        // byte-identical) while the strip's surface still repaints, so any
        // guarded skip is the stale ink back -- whatever the guard is
        // named.
        var method = Host().Method("SetChromeGround");
        Assert.NotEmpty(method.AssignsTo("_chromeGroundPacked"));

        var passes = method.Calls("RefreshShellInactiveInk")
            .Concat(method.Calls("RefreshTabColors"))
            .ToList();
        Assert.True(passes.Count == 2,
            $"expected one ink pass per arm, found {passes.Count}");
        var firstPass = passes.MinBy(p => p.Span.Start)!;
        CallShape.NoReturnBefore(method.Body!, firstPass);

        CallShape.TopLevelWrite(method, "_stripBackdropPacked");
        CallShape.NoGuardedArm(method);
        CallShape.GroundWritePrecedesFirstPass(method);
    }

    [Fact]
    public void TheGroundKeyedBrushes_AreMemoizedOnThePackedGround()
    {
        // Compare-then-refresh, with the key pinned at the node: each memo
        // is assigned the ground it was derived from, inside the same arm
        // that derives the brush. A broken cache (the key write skipped)
        // fails the Single, and a key that ignores the ground (any right
        // side but the ground itself) fails the equality, so a cached
        // answer can only ever be refreshed by a ground that moved.
        var ink = Host().Method("MutedInkBrush");
        var inkGuard = Assert.Single(ink.Body!.Statements.OfType<IfStatementSyntax>());
        var inkCondition = inkGuard.Condition.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.ValueText)
            .ToList();
        Assert.Contains("_mutedInkGround", inkCondition);
        Assert.Contains("ground", inkCondition);
        Assert.Equal("ground",
            Assert.Single(inkGuard.AssignsTo("_mutedInkGround")).Right.ToString());
        Assert.NotEmpty(inkGuard.AssignsTo("_mutedInkBrush"));

        var wash = Host().Method("GroupWashBrush");
        var washGuard = Assert.Single(wash.Body!.Statements.OfType<IfStatementSyntax>());
        var washCondition = washGuard.Condition.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.ValueText)
            .ToList();
        Assert.Contains("_groupWashGround", washCondition);
        Assert.Contains("_stripBackdropPacked", washCondition);
        // Node-exact polarity: the refresh decision is a NotEquals between
        // the two, so a gutted `is null` and an inverted `==` both fail.
        var washCompare = Assert.IsType<BinaryExpressionSyntax>(
            washGuard.Condition.DescendantNodesAndSelf()
                .First(n => n is BinaryExpressionSyntax b
                    && b.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NotEqualsExpression)));
        Assert.Equal("_groupWashGround", washCompare.Left.ToString());
        Assert.Equal("_stripBackdropPacked", washCompare.Right.ToString());
        Assert.Equal("_stripBackdropPacked",
            Assert.Single(washGuard.AssignsTo("_groupWashGround")).Right.ToString());
        Assert.NotEmpty(washGuard.AssignsTo("_groupWashBrush"));
    }

    [Fact]
    public void WindowThemeManager_TheConfigDrivenResolveIsDeferred()
    {
        // The deferral that makes the landing a separate turn from the
        // config fan-out. If the resolve ever moves inline, the landing
        // handler's push stops being the fix and this file's premise needs
        // another look.
        Assert.NotEmpty(Manager().Method("OnConfigChanged").CallsEndingWith("TryEnqueue"));
    }

    // The handler body is half the fix; the two subscription lines that
    // carry the flip into it are the other half. A dropped or
    // lambda-replaced subscription reintroduces the stale ground with every
    // body fact above still green, so both wires are pinned as assignment
    // nodes: an IdentifierNameSyntax right-hand side refuses the lambda
    // shape a text match would wave through.

    [Fact]
    public void MainWindow_TheLandingHandlerIsSubscribedToTheManager()
    {
        var hits = Window().Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddAssignmentExpression)
                        && a.Left is MemberAccessExpressionSyntax m
                        && m.Expression.ToString() == "_themeManager"
                        && m.Name.Identifier.ValueText == "ThemeChanged"
                        && a.Right is IdentifierNameSyntax id
                        && id.Identifier.ValueText == "OnWindowThemeChanged")
            .ToList();
        Assert.True(hits.Count == 1,
            $"expected exactly one _themeManager.ThemeChanged += OnWindowThemeChanged, "
                + $"found {hits.Count}");
    }

    [Fact]
    public void WindowThemeManager_TheConfigDrivenSubscriptionIsLive()
    {
        // Add-only: Dispose's -= is the teardown of the same wire and must
        // not count, so the kind is pinned, not just the operands.
        var hits = Manager().Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddAssignmentExpression)
                        && a.Left is MemberAccessExpressionSyntax m
                        && m.Expression.ToString() == "_configService"
                        && m.Name.Identifier.ValueText == "ConfigChanged"
                        && a.Right is IdentifierNameSyntax id
                        && id.Identifier.ValueText == "OnConfigChanged")
            .ToList();
        Assert.True(hits.Count == 1,
            $"expected exactly one _configService.ConfigChanged += OnConfigChanged, "
                + $"found {hits.Count}");
    }

    [Fact]
    public void ResolveAndNotifyIfChanged_TheResolvePrecedesTheAnnouncement()
    {
        // The announcement is only honest with the freshly resolved answer:
        // OnWindowThemeChanged's push reads _themeManager.IsDarkMode, so a
        // swap that invokes first would push the outgoing polarity's ground
        // exactly when the handler exists to fix that.
        var method = Manager().Method("ResolveAndNotifyIfChanged");
        var resolve = Assert.Single(method.Calls("Resolve"));
        var announce = Assert.Single(method.CallsEndingWith("ThemeChanged?.Invoke"));
        Assert.True(resolve.Span.Start < announce.Span.Start,
            "ThemeChanged is invoked before Resolve()");
    }

    [Fact]
    public void TheMemoizedBrushes_AreOnlyEverWrittenWhole()
    {
        // Whole-instance writes only. A member mutation on a memoized brush
        // (.Opacity =, .Color =, ...) leaks through the cache to every
        // consumer the instance is shared with, and survives until the
        // ground changes -- a stateful brush wearing the cache's permanence.
        // Receivers watched: the three fields themselves, and any local
        // whose initializer is exactly one of them.
        var source = Host().Root;
        var brushFields = new[] { "_mutedInkBrush", "_groupWashBrush", "_shellInactiveTextBrush" };

        Assert.Empty(source.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax m
                        && m.Expression is IdentifierNameSyntax id
                        && brushFields.Contains(id.Identifier.ValueText)));

        var holders = source.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is IdentifierNameSyntax id
                        && brushFields.Contains(id.Identifier.ValueText))
            .Select(v => v.Identifier.ValueText)
            .ToHashSet();
        Assert.Empty(source.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax m
                        && m.Expression is IdentifierNameSyntax id
                        && holders.Contains(id.Identifier.ValueText)));
    }

    [Fact]
    public void TheTwoMutedInkCopies_DeriveWithTheSameConstant()
    {
        // One side memoized, one not: if the formulas or the alpha drift,
        // the strips visibly diverge after a flip. Both copies must call
        // ThemeResolution.ReadableMutedForeground with the shared
        // InactiveInkAlpha constant, and the two constants must spell the
        // same literal.
        var host = Host().Method("MutedInkBrush");
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs").Method("MutedInkBrush");

        foreach (var copy in new[] { host, strip })
        {
            Assert.NotEmpty(copy.CallsEndingWith("ReadableMutedForeground"));
            Assert.NotEmpty(copy.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(id => id.Identifier.ValueText == "InactiveInkAlpha"));
        }

        var hostAlpha = Host().Field("InactiveInkAlpha");
        var stripAlpha = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs")
            .Field("InactiveInkAlpha");
        Assert.Equal(
            hostAlpha.Variable.Initializer!.Value.ToString(),
            stripAlpha.Variable.Initializer!.Value.ToString());
    }
}

/// <summary>
/// The shared unconditionality checker behind the theme-flip facts. One
/// implementation, many pinned shapes: every guard shape this file has to
/// reject -- braced or braceless wraps, lambda assignments, returns above a
/// call in any nesting, ground-conditioned skips, reordered writes -- must
/// fail exactly one of these checks, or a future shape walks through.
/// </summary>
internal static class CallShape
{
    // (1) The call is a direct top-level ExpressionStatement of the method
    // body. A braced if/try/using/lock/switch interposes its own block; a
    // braceless if or an expression-lambda assignment interposes itself.
    // Containment walks pass all of them; the parent chain cannot.
    // (2) No return anywhere in the body's subtree -- else clauses
    // included -- closes before the call starts.
    public static void TopLevelCall(MethodDeclarationSyntax method, string callee)
    {
        var body = method.Body!;
        var calls = method.Calls(callee);
        Assert.True(calls.Count == 1,
            $"expected exactly one {callee} call in {method.Identifier.ValueText}, "
                + $"found {calls.Count}");
        var call = calls[0];

        Assert.IsType<ExpressionStatementSyntax>(call.Parent);
        var callStatement = (StatementSyntax)call.Parent;
        Assert.Same(body, callStatement.Parent);
        NoReturnBefore(body, callStatement);
    }

    // (2) alone, for a call that legitimately lives inside an arm.
    public static void NoReturnBefore(SyntaxNode scope, SyntaxNode call)
    {
        Assert.Empty(scope.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
            .Where(r => r.Span.End <= call.Span.Start));
    }

    // (3) Structural guard sweep: every if in the method EXCEPT the named
    // shell-dispatch if (whose condition is spelled exactly below) must
    // hold neither a return nor any invocation in ANY arm -- statement,
    // else clause, or braceless embedded statement. Name-independent, so a
    // guard wearing a fresh local's name (`if (unchanged) return;`) is
    // caught the same as the memo-shaped ones. The three-name watch set
    // stays as belt, in case the exemption above is ever widened: an if
    // conditioned on the ground memo, the landed value, or the input memo
    // may hold no return and no ink pass whatever its shape.
    public static void NoGuardedArm(MethodDeclarationSyntax method)
    {
        foreach (var guard in method.Body!.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (guard.Condition.ToString() == "_shellActiveTextBrush is not null")
                continue;

            Assert.Empty(GuardArmReturns(guard.Statement));
            Assert.Empty(GuardArmInvocations(guard.Statement));
            if (guard.Else is { } elseClause)
            {
                Assert.Empty(GuardArmReturns(elseClause.Statement));
                Assert.Empty(GuardArmInvocations(elseClause.Statement));
            }
        }

        var watched = method.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText is "groundChanged"
                    or "_stripBackdropPacked" or "_chromeGroundPacked"));
        foreach (var guard in watched)
        {
            Assert.Empty(GuardArmReturns(guard.Statement));
            Assert.Empty(GuardArmPasses(guard.Statement));
            if (guard.Else is { } elseClause)
            {
                Assert.Empty(GuardArmReturns(elseClause.Statement));
                Assert.Empty(GuardArmPasses(elseClause.Statement));
            }
        }
    }

    // (5) The named write is a direct top-level statement of the method
    // body: the gated `if (groundChanged) write;` and the ternary spelling
    // `field = cond ? a : b;` both interpose -- an if or a conditional
    // expression between the write and the body -- and fail here.
    public static void TopLevelWrite(MethodDeclarationSyntax method, string field)
    {
        var body = method.Body!;
        var landing = Assert.Single(method.AssignsTo(field));
        Assert.IsType<ExpressionStatementSyntax>(landing.Parent);
        var writeStatement = (StatementSyntax)landing.Parent;
        Assert.Same(body, writeStatement.Parent);
        Assert.Empty(landing.Ancestors().TakeWhile(a => a != body)
            .OfType<IfStatementSyntax>());
        // The ternary spelling keeps the statement top-level but carries
        // the conditional inside it, so the statement's own subtree is
        // swept as well as the path above it.
        Assert.Empty(writeStatement.DescendantNodesAndSelf()
            .OfType<ConditionalExpressionSyntax>());
    }

    // (4) The ground's landed value must precede the first pass that reads
    // it. The shell arm's own pass legitimately runs before the default
    // arm's landing (that arm exits first), so the readers that matter are
    // the default arm's: a swap that lands the write after them scores
    // them against the outgoing value.
    public static void GroundWritePrecedesFirstPass(MethodDeclarationSyntax method)
    {
        var landing = Assert.Single(method.AssignsTo("_stripBackdropPacked"));
        var readers = method.Calls("RefreshTabColors")
            .Concat(method.Calls("RefreshGroupFieldWash"))
            .ToList();
        Assert.NotEmpty(readers);
        var firstReader = readers.MinBy(p => p.Span.Start)!;
        Assert.True(landing.Span.Start < firstReader.Span.Start,
            "the ground write lands after the first pass that reads it");
    }

    // DescendantNodesAndSelf: a braceless embedded arm IS the return or
    // the pass, and a bare `return;` has no descendants of its own.
    private static IEnumerable<ReturnStatementSyntax> GuardArmReturns(StatementSyntax arm)
        => arm.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>();

    private static IEnumerable<InvocationExpressionSyntax> GuardArmPasses(StatementSyntax arm)
        => arm.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() is "RefreshShellInactiveInk"
                or "RefreshTabColors" or "RefreshGroupFieldWash");

    private static IEnumerable<InvocationExpressionSyntax> GuardArmInvocations(StatementSyntax arm)
        => arm.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>();
}
