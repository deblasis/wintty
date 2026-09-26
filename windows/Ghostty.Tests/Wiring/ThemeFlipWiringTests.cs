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
}
