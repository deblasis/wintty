using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The only title checks left that read source. The behaviour lives in Core
/// and is tested there with fakes: PaneTitleForwarderTests (which pane names
/// the tab), TabTitlePerTabTests (which tab a title lands on) and
/// WindowTitleFollowerTests (the caption). What those cannot see is whether
/// the WinUI classes, which this assembly cannot load, hand their events to
/// them at all.
/// </summary>
public class TabTitleWiringTests
{
    /// <summary>
    /// PaneHost feeds its forwarder: every terminal is tracked and untracked
    /// in the same block as its directory wiring, and a focus change tells
    /// the forwarder. Anchored on the directory wiring rather than on a
    /// method name, so moving the per-terminal wiring into a helper stays
    /// green only if the title moves with it.
    /// </summary>
    [Fact]
    public void PaneHost_FeedsItsTitleForwarder()
    {
        var root = ShellSource.Load("Panes.PaneHost.cs").Root;

        InvocationExpressionSyntax OneCall(string callee)
        {
            var found = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression.ToString() == callee).ToList();
            Assert.True(found.Count == 1, $"expected one call to {callee}, found {found.Count}");
            return found[0];
        }

        AssignmentExpressionSyntax PwdWiring(SyntaxKind kind)
            => Assert.Single(root.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => a.IsKind(kind) && a.Left.ToString().EndsWith(".PwdChanged"));

        BlockSyntax BlockOf(SyntaxNode n) => n.Ancestors().OfType<BlockSyntax>().First();

        Assert.Same(BlockOf(PwdWiring(SyntaxKind.AddAssignmentExpression)), BlockOf(OneCall("_titleForwarder.Track")));
        Assert.Same(BlockOf(PwdWiring(SyntaxKind.SubtractAssignmentExpression)), BlockOf(OneCall("_titleForwarder.Untrack")));

        var focusHandler = Assert.Single(root.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Left.ToString() == "LeafFocused");
        Assert.Same(focusHandler, OneCall("_titleForwarder.ActiveChanged").Ancestors()
            .OfType<AssignmentExpressionSyntax>().First());
    }

    /// <summary>
    /// TitleBarCoordinator hands the caption to WindowTitleFollower and
    /// writes Window.Title nowhere else, and hooks no pane title of its own
    /// (the hook that caused wintty#1128 and wintty#1129).
    /// </summary>
    [Fact]
    public void TitleBarCoordinator_DelegatesTheCaption_AndHooksNoPane()
    {
        var root = ShellSource.Load("Shell.TitleBarCoordinator.cs").Root;

        Assert.Single(root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            c => c.Type.ToString() == "WindowTitleFollower");
        var titleWrites = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Title")).ToList();
        var write = Assert.Single(titleWrites);
        Assert.NotNull(write.Ancestors().OfType<ObjectCreationExpressionSyntax>()
            .FirstOrDefault(c => c.Type.ToString() == "WindowTitleFollower"));

        Assert.Empty(root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax m
                        && m.Name.Identifier.ValueText is "TitleChanged" or "LeafFocused"));
    }
}
