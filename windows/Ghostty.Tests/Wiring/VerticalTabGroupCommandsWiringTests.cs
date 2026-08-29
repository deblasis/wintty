using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The group commands' entry points, as wiring guards: the member menu's
/// group block (one implementation shared by both hosts), the vertical
/// header menu, the header right-click that must NOT fall through to the
/// strip-wide menu, and the collapse round trip through the strip where
/// the focus re-home lives. Announcements: TabPinZoneWiringTests. Model
/// half: TabManagerPinGroupTests.
/// </summary>
public class VerticalTabGroupCommandsWiringTests
{
    private static ShellSource Builder() => ShellSource.Load("Tabs.TabContextMenuBuilder.cs");
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
    private static ShellSource Router() => ShellSource.Load("Input.PaneActionRouter.cs");
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    private static AssignmentExpressionSyntax Assign(SyntaxNode node, string left) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == left);

    /// <summary>
    /// The member menu's group block, one implementation for both hosts
    /// (5.4). Pinned tabs are offered nothing -- the manager refuses their
    /// membership; the submenu lists every group BUT the tab's own, where a
    /// join is the no-op the router refuses.
    /// </summary>
    [Fact]
    public void TheMemberMenu_CarriesTheGroupBlock_AndBothHostsShareIt()
    {
        var build = Builder().Method("Build");

        var pinned = build.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!tab.IsPinned");
        var block = pinned.Statement;

        var ungrouped = block.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "tab.Group is null");
        Assert.Contains("New Group With Tab", ungrouped.ToString());
        Assert.Contains("requestNewGroupWithTab(tab)", ungrouped.ToString());
        var groupedArm = block.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString() == "tab.Group is null").Else!.Statement;
        Assert.Contains("Remove from Group", groupedArm.ToString());
        Assert.Contains("requestRemoveFromGroup(tab)", groupedArm.ToString());

        // The submenu's run list excludes the tab's own group, and each
        // entry routes the two-argument join.
        var filter = block.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!ReferenceEquals(g, tab.Group)");
        Assert.Contains("manager.Groups", filter.Ancestors().OfType<ForEachStatementSyntax>().First().ToString());
        var submenu = block.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "MenuFlyoutSubItem");
        Assert.Contains("Add to Group", submenu.ToString());

        // Each entry routes the two-argument join, and the click closes
        // over a per-run copy of the group (`var target = g`), not the
        // loop variable every entry would otherwise share.
        var run = block.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Single(f => f.Expression.ToString() == "others");
        Assert.Contains("var target = g", run.Statement.ToString());
        Assert.Contains("Text = g.Title", run.Statement.ToString());
        Assert.Contains("requestAddToGroup(tab, target)", run.Statement.ToString());

        // Both hosts pass the same three router methods: one menu, two
        // modes, no divergent copy of the block.
        foreach (var host in new[] { "Tabs.VerticalTabHost.xaml.cs", "Tabs.TabHost.xaml.cs" })
        {
            var source = ShellSource.Load(host);
            Assert.Contains("requestNewGroupWithTab: _router.RequestNewGroupWithTab",
                source.Root.ToString());
            Assert.Contains("requestAddToGroup: _router.RequestAddToGroup",
                source.Root.ToString());
            Assert.Contains("requestRemoveFromGroup: _router.RequestRemoveFromGroup",
                source.Root.ToString());
        }
    }

    /// <summary>
    /// The header menu's collapse routes with the FLIPPED bit like every
    /// toggle, the label re-reads the live bit on Opening (the chevron can
    /// toggle between build and open), and Close Group greys out exactly
    /// when the group holds every tab -- the Move Tab to New Window rule.
    /// </summary>
    [Fact]
    public void TheHeaderMenu_TogglesThroughTheRouter_AndGreysCloseAtTheLastGroup()
    {
        var menu = Builder().Method("BuildGroupMenu");

        var labels = menu.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Where(c => c.Condition.ToString() == "group.IsCollapsed").ToList();
        Assert.Equal(2, labels.Count);
        foreach (var label in labels)
        {
            Assert.Equal("\"Expand Group\"", label.WhenTrue.ToString());
            Assert.Equal("\"Collapse Group\"", label.WhenFalse.ToString());
        }

        var collapse = menu.Calls("requestCollapseGroup").Single();
        Assert.Equal("group", collapse.Arg(0));
        Assert.Equal("!group.IsCollapsed", collapse.Arg(1));

        Assert.Contains("Dissolve Group", menu.ToString());
        Assert.Contains("requestDissolveGroup(group)", menu.ToString());

        var enabled = menu.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "close.IsEnabled").ToList();
        Assert.Equal(2, enabled.Count);
        foreach (var assignment in enabled)
            Assert.Equal("!manager.GroupHoldsEveryTab(group)", assignment.Right.ToString());
        Assert.Contains("requestCloseGroup(group)", menu.ToString());

        // One of the two greys at build time; the other lives in the
        // Opening pass, so a chevron toggle between build and open cannot
        // leave a live close greyed (or a dead one enabled).
        Assert.Contains(
            enabled,
            a => a.Ancestors().OfType<AssignmentExpressionSyntax>()
                .Any(handler => handler.Left.ToString() == "flyout.Opening"));
    }

    /// <summary>
    /// A header right-click used to fall through to the strip-wide menu:
    /// the tab resolver answers null for a Tag that is a TabGroup, and the
    /// old code read that as "no row here". The group branch now comes
    /// first, handles the event, and returns.
    /// </summary>
    [Fact]
    public void HeaderRightClick_BuildsTheGroupMenu_NeverTheStripMenu()
    {
        var host = ShellSource.Load("Tabs.VerticalTabHost.xaml.cs");
        var requested = host.Method("OnStripContextRequested");

        var groupBranch = requested.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_strip.GroupFromElement(source) is { } group");
        Assert.Contains("TabContextMenuBuilder.BuildGroupMenu", groupBranch.Statement.ToString());
        Assert.Contains(
            groupBranch.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
                .Select(r => r.ToString()),
            s => s == "return;");
        Assert.Equal("true", Assign(groupBranch.Statement, "e.Handled").Right.ToString());

        var stripMenu = requested.Calls("StripContextMenuBuilder.Build").Single();
        Assert.True(groupBranch.Span.Start < stripMenu.Span.Start,
            "the group branch must precede the strip-menu fallthrough, or a " +
            "header right-click opens the strip menu again");

        // The resolver asks the Tag for a TabGroup; the tab resolver asks
        // the same slot for a TabModel. The pair is what routes the menus.
        Assert.Contains(
            "VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source)?.Tag as TabGroup",
            Strip().Method("GroupFromElement").ToString());
    }

    /// <summary>
    /// Collapse commands round-trip through the strip because only the
    /// strip knows where keyboard focus sits: the router guards and
    /// announces, the window forwards to the vertical host, and the strip's
    /// command entry re-homes focus under the folding group before the
    /// chevron's own toggle runs. Expand hides nothing and re-homes nothing.
    /// </summary>
    [Fact]
    public void CollapseCommands_RoundTrip_ThroughTheStrip_AndRehomeUnderTheFoldingGroup()
    {
        var router = Router().Method("RequestCollapseGroup");
        var noOp = router.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "group.IsCollapsed == collapsed");
        var forward = router.Calls("GroupCollapseRequested?.Invoke").Single();
        var announce = router.Calls("GroupChangedFromCommand?.Invoke").Single();
        Assert.True(noOp.Span.Start < forward.Span.Start && forward.Span.Start < announce.Span.Start,
            "a same-state command must return before it forwards or announces");

        // The forward can be stood down by the strip's drag fence, so the
        // landed bit is re-read against the target between them: a refused
        // op narrates nothing.
        var gate = router.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "group.IsCollapsed != collapsed");
        Assert.True(forward.Span.Start < gate.Span.Start && gate.Span.Start < announce.Span.Start,
            "a stood-down forward must return before the announce");

        var strip = Strip();
        var command = strip.Method("ToggleGroupFromCommand");
        var arms = command.DescendantNodes().OfType<IfStatementSyntax>().ToList();
        Assert.Equal("_drag is not null", arms[0].Condition.ToString());
        Assert.Equal("group.IsCollapsed == collapsed", arms[1].Condition.ToString());
        Assert.Equal("collapsed", arms[2].Condition.ToString());
        Assert.True(arms[2].Span.Start < command.Calls("RestoreFocusUnder").Single().Span.Start,
            "the re-home runs only on the collapse arm, under the fold");
        Assert.Single(command.Calls("ToggleGroup"));

        var window = Window();
        var forward2 = window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_router.GroupCollapseRequested");
        Assert.Contains("VerticalTabHost", forward2.Right.ToString());
        Assert.Contains("CollapseGroupFromCommand", forward2.Right.ToString());
    }

    /// <summary>
    /// JoinGroup delegates to GroupTabs, which silently skips pinned
    /// members, so a refused join is invisible in the manager either way
    /// and the only observable is the raise: without the guard, a pinned
    /// join narrates a membership change that never happened. (A
    /// behavioural fact is out of reach here -- the shell assembly cannot
    /// load into the test host -- so this pins the shape that produces
    /// the behaviour. This is also the guard 2b's palette exposure sits
    /// behind: today's menus cannot offer it for a pinned tab.)
    /// </summary>
    [Fact]
    public void RequestAddToGroup_RefusesAPinnedTab_BeforeJoinAndRaise()
    {
        var add = Router().Method("RequestAddToGroup");
        var pinned = add.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "tab.IsPinned");
        Assert.True(
            pinned.Span.Start < add.Calls("_tabs.JoinGroup").Single().Span.Start,
            "the pinned refusal must precede the join");
        Assert.True(
            pinned.Span.Start < add.Calls("GroupChangedFromCommand?.Invoke").Single().Span.Start,
            "the pinned refusal must precede the raise, or the refusal narrates");
    }

    /// <summary>
    /// The host wires the strip's keyboard command to the router, closing
    /// the round trip the keyboard gesture opened; the pointer chevron has
    /// no such wiring because it toggles in place and stays silent.
    /// </summary>
    [Fact]
    public void TheKeyboardRoute_IsWiredOnce_ToTheRoutersCollapse()
    {
        var host = ShellSource.Load("Tabs.VerticalTabHost.xaml.cs");
        var wiring = host.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_strip.GroupToggleFromCommandRequested");
        Assert.Contains("RequestCollapseGroup", wiring.Right.ToString());

        // One wiring, not one per gesture: the strip raises from the key
        // path only (2b adds the UIA pattern to the same event).
        Assert.Single(Strip().Root.Calls("GroupToggleFromCommandRequested?.Invoke"));
    }

    /// <summary>
    /// Close Group is one announcement for the group-sized intent and a
    /// sequential walk through the per-tab confirmation path. The declined
    /// guard is the loop's exit: if the first member survived a close
    /// attempt, the user said no, and trying it again would loop forever.
    /// </summary>
    [Fact]
    public void SequentialClose_AnnouncesOnce_AndStopsWhenTheUserDeclines()
    {
        var window = Window();
        var subscription = window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_router.GroupCloseRequested");

        var announce = subscription.Right.Calls("UiaAnnouncer.Announce").Single();
        var loop = subscription.Right.DescendantNodes().OfType<WhileStatementSyntax>().Single();
        Assert.Contains("Groups.Contains(group)", loop.Condition.ToString());
        Assert.True(announce.Span.Start < loop.Span.Start,
            "the group intent announces once, before the sequential walk");

        Assert.Contains("_tabHost.RequestCloseTabAsync", loop.ToString());
        var declined = loop.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("ReferenceEquals(members[0], first)"));
        Assert.Contains(
            "members.Count > 0 && ReferenceEquals(members[0], first)",
            declined.Condition.ToString());
        Assert.Contains(
            declined.Statement.DescendantNodesAndSelf().OfType<BreakStatementSyntax>()
                .Select(b => b.ToString()),
            s => s == "break;");
    }
}
