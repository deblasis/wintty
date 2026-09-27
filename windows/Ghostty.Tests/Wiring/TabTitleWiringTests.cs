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

    /// <summary>
    /// The set_tab_title event carries the surface that sent it, and the
    /// host hands the event that surface. A title is the surface's request
    /// about its own tab, and the surface's tab is not necessarily the
    /// selected one (wintty#1169): the sender used to be dropped here, and
    /// with it went the only fact the window needed to route the title.
    /// </summary>
    [Fact]
    public void TheSetTabTitleEvent_CarriesTheSendingSurface()
    {
        var host = ShellSource.Load("Hosting.GhosttyHost.cs");

        var declaration = Assert.Single(host.Root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>(),
            v => v.Variables.Any(x => x.Identifier.ValueText == "SetTabTitleRequested"));
        Assert.Equal("Action<TerminalControl, string>?", declaration.Type.ToString());

        var invoke = host.Case("OnAction", "SetTabTitle")
            .Call("owner.SetTabTitleRequested?.Invoke");
        Assert.Equal("control", invoke.Arg(0));
        Assert.Equal("title", invoke.Arg(1));
    }

    /// <summary>
    /// The handler names the tab that owns the sending surface, never the
    /// selected one: a set_tab_title from a background pane used to rename
    /// whichever tab was active (wintty#1169). The title is remote text at
    /// the top tier, so it is refused unless it is plain; empty still
    /// clears, and a hostile title leaves the override untouched. The
    /// surface-to-tab walk lives once, shared with PresentSurface, so the
    /// two answers about which tab a surface belongs to cannot drift.
    /// </summary>
    [Fact]
    public void TheSetTabTitleHandler_NamesTheSurfacesOwnTab()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");

        var subscribe = Assert.Single(window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                && a.Left.ToString() == "_host.SetTabTitleRequested");

        // The defect itself, named so it cannot come back: nothing inside
        // the surface-title subscription retitles the selected tab. (The
        // prompt-title dialog keeps its ActiveTab write: that rename is the
        // user acting on the selected tab, not a surface acting on its own.)
        Assert.Empty(subscribe.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_tabManager.ActiveTab.UserOverrideTitle"));

        // And that dialog's write is the only one in the window at all.
        Assert.Single(window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_tabManager.ActiveTab.UserOverrideTitle"));

        // One subscription in the whole corpus: a second subscriber would
        // route titles somewhere this file cannot see.
        Assert.Single(ShellSource.AllFiles()
            .SelectMany(f => f.Root.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                            && a.Left.ToString() == "_host.SetTabTitleRequested")));

        // The override is written through the walk, on the tab it returns
        // for the carried surface, and an empty title still clears.
        var walk = Assert.Single(subscribe.DescendantNodes()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TabContaining");
        Assert.Equal("control", walk.Arg(0));

        var write = Assert.Single(subscribe.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a != subscribe && a.Left.ToString() == "tab.UserOverrideTitle"));
        // Remote text at the top tier: the gate travels with the write,
        // and a hostile title leaves the override untouched.
        Assert.Equal(
            "string.IsNullOrWhiteSpace(title) ? null : TabLabel.IsPlain(title) ? title : tab.UserOverrideTitle",
            write.Right.ToString());

        // One walk, two callers: PresentSurface resolves the same way.
        var helper = window.Method("TabContaining");
        Assert.NotEmpty(helper.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("ReferenceEquals", System.StringComparison.Ordinal)
                        && i.Arg(0).ToString() == "leaf.Terminal()"));
        Assert.True(helper.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression?.ToString() == "null"));
        Assert.Single(window.Method("PresentSurface").DescendantNodes()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TabContaining");

        // The walk is the real walk: every tab, its leaves, the tab it
        // matches, null on miss. A walk over only the active tab would
        // quietly reintroduce the defect this test exists for.
        Assert.NotEmpty(helper.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Where(f => f.Expression.ToString() == "_tabManager.Tabs"));
        Assert.NotEmpty(helper.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("PaneTree.Leaves", System.StringComparison.Ordinal)));
        Assert.NotEmpty(helper.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(r => r.Expression?.ToString() == "tab"));
    }
}
