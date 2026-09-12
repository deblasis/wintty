using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Behaviour facts live in SessionTreeTests and
/// SessionProfileResolverTests (drop, collapse, keep, never-resaved);
/// these are the wiring guards for the hops in the WinUI restorer a
/// unit test cannot see: the drop decision is wired into the rebuild,
/// an emptied tab is not rebuilt at all, the restore says so exactly
/// once, and only the restore site hands the restorer a logger (the
/// reopen/duplicate sites drop silently by design).
/// </summary>
public class SessionRestoreDropWiringTests
{
    // BuildTab has two overloads now: the public single-tab entry and
    // the private worker BuildTabs also drives (it carries the dropped
    // names out). The drop wiring lives in the worker.
    private static MethodDeclarationSyntax WorkerBuildTab(ShellSource src)
    {
        var both = src.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "BuildTab")
            .ToList();
        Assert.True(both.Count == 2,
            $"expected the public entry and the private worker, found {both.Count}");
        return both.Single(m => m.ParameterList.Parameters.Count == 2);
    }

    private static bool ReturnsNull(ReturnStatementSyntax r) =>
        r.Expression is LiteralExpressionSyntax l
            && l.IsKind(SyntaxKind.NullLiteralExpression);

    [Fact]
    public void BuildTab_RefusesThroughTheResolver_BeforeResolving()
    {
        var buildTab = WorkerBuildTab(ShellSource.Load("Session.SessionRestorer.cs"));

        var rebuild = buildTab.Call("SessionTree.RebuildTree");
        var lambda = Assert.IsAssignableFrom<LambdaExpressionSyntax>(rebuild.ArgExpression(1));

        // The refusal IS the resolver's decision, asked before anything
        // spawns, and its arm is the lambda's one null return.
        var ask = lambda.Calls("SessionProfileResolver.ShouldDropLeaf");
        Assert.Single(ask);
        var resolve = lambda.Calls("SessionProfileResolver.ResolveLeaf");
        Assert.Single(resolve);
        Assert.True(ask[0].SpanStart < resolve[0].SpanStart,
            "the drop decision must be asked before the leaf resolves");

        var nullReturns = lambda.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(ReturnsNull).ToList();
        Assert.Single(nullReturns);
    }

    [Fact]
    public void BuildTab_AnAllRefusedTree_BuildsNoTab()
    {
        var buildTab = WorkerBuildTab(ShellSource.Load("Session.SessionRestorer.cs"));

        // RebuildTree hands back null when every leaf was refused; the
        // tab must translate that into "no tab" so it is neither
        // restored nor, never built, re-saved. The null return has to
        // sit after the rebuild (the one inside the lambda is the
        // per-leaf refusal, not the tab's).
        var rebuild = buildTab.Call("SessionTree.RebuildTree");
        Assert.Contains(buildTab.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => ReturnsNull(r) && r.SpanStart > rebuild.Span.End);
    }

    [Fact]
    public void BuildTabs_LoggedOncePerRestore_NotPerLeaf()
    {
        var buildTabs = ShellSource.Load("Session.SessionRestorer.cs").Method("BuildTabs");

        var notice = buildTabs.Calls("_logger.LogSessionRestoreDroppedLeaves");
        Assert.Single(notice);

        // And only when something actually went: the call is guarded by
        // the drop count, not fired unconditionally.
        var guard = notice[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(guard);
        Assert.Contains("dropped.Count", guard!.Condition.ToString());
    }

    [Fact]
    public void OnlyTheRestoreSite_HandsTheRestorerALogger()
    {
        var mainWindow = ShellSource.Load("MainWindow.xaml.cs");
        var restorers = mainWindow.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString().Contains("SessionRestorer")).ToList();
        Assert.True(restorers.Count == 3,
            $"expected restore, reopen and duplicate sites, found {restorers.Count}");

        // The restore site is the one that logs; reopen and duplicate
        // capture live tabs, where a refusal is not a real state.
        var logged = restorers.Where(o => o.ArgumentList!.Arguments.Count == 3
            && o.ArgumentList.Arguments[2].ToString().Contains("CreateLogger")).ToList();
        Assert.Single(logged);
    }

    [Fact]
    public void AnEmptyRestore_FallsThroughToTheFreshDefaultTab()
    {
        var mainWindow = ShellSource.Load("MainWindow.xaml.cs");

        // The chain's last hop: when every tab of a saved window is
        // dropped, BuildTabs comes back empty and the ctor must keep the
        // fresh-start default-tab path it already had. The guard on the
        // restoredTabs assignment is what keeps an empty list from
        // seeding an empty window.
        var assignment = mainWindow.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "restoredTabs"
                        && a.Right.ToString() == "built")
            .ToList();
        Assert.Single(assignment);
        var guard = assignment[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(guard);
        Assert.Contains("built.Count", guard!.Condition.ToString());
    }

    [Fact]
    public void ReopenAndDuplicate_RideTheSameDropDecision()
    {
        var mainWindow = ShellSource.Load("MainWindow.xaml.cs");

        // A leaf the rule KEEPS (an ordinary env-var one-liner, say)
        // must still duplicate and reopen: both call sites go through
        // the same BuildTab worker the restore does, so one
        // ShouldDropLeaf decision covers all three paths. The chained
        // spelling in DuplicateTab (restorer on one line, .BuildTab on
        // the next) needs the suffix match.
        var reopen = mainWindow.Method("ReopenClosedTab");
        Assert.Single(reopen.Calls("restorer.BuildTab"));

        var duplicate = mainWindow.Method("DuplicateTab");
        Assert.Single(duplicate.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(".BuildTab", System.StringComparison.Ordinal)));
    }
}
