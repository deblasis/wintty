using System;
using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// <see cref="TabManager.GroupsChanged"/>: the one signal a consumer can
/// hear a group through.
///
/// The defect it answers is persistence, not presentation: a saved
/// session carries each group's id, title, color and collapse bit
/// (<c>SessionCapture.CaptureGroups</c>), and before this event existed
/// none of those reached the file -- a rename or a collapse writes a
/// <see cref="TabGroup"/> property, not a tab, and a dissolve removes a
/// registry entry rather than a tab. The strips were never the audience;
/// they re-read the projection.
/// </summary>
public class TabGroupSignalTests
{
    /// <param name="tabs">How many tabs the manager should end up holding.</param>
    private static TabManager NewManager(int tabs = 2)
    {
        var hosts = new Queue<IPaneHost>();
        for (int i = 0; i < tabs; i++) hosts.Enqueue(new FakePaneHost());
        var mgr = new TabManager((_) => hosts.Dequeue());
        for (int i = 1; i < tabs; i++) mgr.NewTab();
        return mgr;
    }

    [Fact]
    public void Grouping_joins_and_dissolving_raises()
    {
        var mgr = NewManager();
        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;

        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[0], mgr.Tabs[1]], group);
        Assert.Equal(1, raised);

        mgr.DissolveGroup(group);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Ungrouping_one_member_raises()
    {
        var mgr = NewManager();
        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[0], mgr.Tabs[1]], group);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        mgr.Ungroup(mgr.Tabs[1]);

        Assert.Equal(1, raised);
        Assert.Null(mgr.Tabs[1].Group);
    }

    [Fact]
    public void Collapsing_and_expanding_raises()
    {
        var mgr = NewManager(tabs: 1);
        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[0]], group);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        mgr.CollapseGroup(group, true);
        mgr.CollapseGroup(group, false);

        Assert.Equal(2, raised);
        Assert.False(group.IsCollapsed);
    }

    [Fact]
    public void Renaming_and_recoloring_raises()
    {
        var mgr = NewManager(tabs: 1);
        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[0]], group);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        group.Title = "build";
        group.Color = TabColor.Green;

        // The group's own INPC is the carrier; the manager holds the
        // registry subscription, and re-raising from there is what a
        // session save needs -- neither property write touches a tab.
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_no_op_collapse_does_not_raise()
    {
        var mgr = NewManager(tabs: 1);
        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[0]], group);
        mgr.CollapseGroup(group, true);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        mgr.CollapseGroup(group, true);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Tab_activity_does_not_raise()
    {
        var mgr = NewManager(tabs: 3);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        mgr.Activate(mgr.Tabs[0]);
        mgr.Move(0, 1);
        mgr.Tabs[1].UserOverrideTitle = "build";

        // The signal is coarse, not indiscriminate: what it must not
        // become is "every tab event", which would turn the session
        // debounce into a write per prompt.
        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_dissolved_group_stops_raising()
    {
        var mgr = NewManager(tabs: 1);
        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[0]], group);
        mgr.DissolveGroup(group);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        group.Title = "after the dissolve";

        // The registry drops its subscription, so a stale reference held by
        // a dialog that outlived the dissolve cannot keep the manager
        // raising for a group that is no longer saved.
        Assert.Equal(0, raised);
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void Closing_the_last_member_drops_the_registry_entry()
    {
        var mgr = NewManager();
        var group = new TabGroup();
        mgr.GroupTabs([mgr.Tabs[1]], group);

        var raised = 0;
        mgr.GroupsChanged += (_, _) => raised++;
        mgr.CloseTab(mgr.Tabs[1]);

        // Deliberately no GroupsChanged here: CloseTab raises TabRemoved,
        // which is the signal the session save already hears, and the
        // capture reads the registry as it is at write time. A second
        // raise would be the same write twice.
        Assert.Equal(0, raised);
        Assert.Empty(mgr.Groups);
    }
}
