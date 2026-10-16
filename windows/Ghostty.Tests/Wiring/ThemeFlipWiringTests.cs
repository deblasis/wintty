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
        // The landing handler is the only place the deferred flip arrives,
        // and ApplyTheme alone flips surfaces. The ink's ground reaches the
        // strips only through RefreshBackdropChrome, so the handler owes it
        // the same way every other input that can move the ground owes it.
        var handler = Window().Method("OnWindowThemeChanged");
        Assert.NotEmpty(handler.Calls("ApplyTheme"));

        // Unconditional, and exactly once: the push rides no guard. The
        // call's innermost enclosing block must be the handler's own body
        // (an if/try/using would interpose its own block), and no return
        // may sit above it. `if (false) { RefreshBackdropChrome(); }` and
        // an early return both keep a presence-only pin green.
        var repaints = handler.Calls("RefreshBackdropChrome");
        Assert.True(repaints.Count == 1,
            $"expected exactly one RefreshBackdropChrome call, found {repaints.Count}");
        var call = repaints[0];

        var body = handler.Body!;
        var innermostBlock = call.Ancestors().OfType<BlockSyntax>().First();
        Assert.True(innermostBlock == body,
            "RefreshBackdropChrome sits inside a guard; the landing push must be unconditional");

        var callStatement = call.Ancestors().OfType<StatementSyntax>().First();
        var seenCall = false;
        foreach (var statement in body.Statements)
        {
            if (statement.Span.Contains(callStatement.Span)) { seenCall = true; break; }
            Assert.IsNotType<ReturnStatementSyntax>(statement);
        }
        Assert.True(seenCall, "RefreshBackdropChrome not found among the body's statements");
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
    public void SetChromeGround_TheChangedArmRepaintsTheTitles()
    {
        // The push only re-derives ink when the ground actually moved, and
        // both arms must reach an ink pass: the shell path through
        // RefreshShellInactiveInk, the default path by landing the ground
        // and re-running the pass that reads it.
        var method = Host().Method("SetChromeGround");
        Assert.NotEmpty(method.AssignsTo("_chromeGroundPacked"));
        Assert.NotEmpty(method.Calls("RefreshShellInactiveInk"));
        Assert.NotEmpty(method.AssignsTo("_stripBackdropPacked"));
        Assert.NotEmpty(method.Calls("RefreshTabColors"));

        // Both ink passes must FOLLOW the early-return guard, not merely
        // exist: hoisting either above `if (!groundChanged) return;` turns
        // the memo into paint-on-every-push and repaints nothing when the
        // ground did move past a caller that skipped.
        var guard = Assert.Single(method.Body!.Statements.OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("groundChanged", StringComparison.Ordinal)
                        && i.Statement.ToString().Contains("return", StringComparison.Ordinal)));
        foreach (var pass in method.Calls("RefreshShellInactiveInk")
                     .Concat(method.Calls("RefreshTabColors")))
            Assert.True(guard.Span.Start < pass.Span.Start,
                $"{pass.CalleeText()} sits above the groundChanged guard");
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
}
