using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerTests
{
    private static TabManager NewManager(out List<FakePaneHost> hosts)
    {
        var hostList = new List<FakePaneHost>();
        var mgr = new TabManager(() =>
        {
            var h = new FakePaneHost();
            hostList.Add(h);
            return h;
        });
        hosts = hostList;
        return mgr;
    }

    [Fact]
    public void Construction_creates_one_tab_and_activates_it()
    {
        var mgr = NewManager(out var hosts);
        Assert.Single(mgr.Tabs);
        Assert.Same(mgr.Tabs[0], mgr.ActiveTab);
        Assert.Single(hosts);
    }

    [Fact]
    public void NewTab_appends_and_activates_and_raises_TabAdded()
    {
        var mgr = NewManager(out _);
        TabModel? added = null;
        mgr.TabAdded += (_, t) => added = t;

        mgr.NewTab();

        Assert.Equal(2, mgr.Tabs.Count);
        Assert.Same(mgr.Tabs[1], mgr.ActiveTab);
        Assert.Same(mgr.Tabs[1], added);
    }

    [Fact]
    public void RequestCloseActive_with_multi_pane_closes_pane_only()
    {
        var mgr = NewManager(out var hosts);
        hosts[0].SetPaneCount(2);
        var beforeTabCount = mgr.Tabs.Count;

        mgr.RequestCloseActive();

        Assert.Equal(beforeTabCount, mgr.Tabs.Count);
        Assert.Equal(1, hosts[0].CloseActiveCalls);
    }

    [Fact]
    public void RequestCloseActive_with_one_pane_closes_tab()
    {
        var mgr = NewManager(out _);
        mgr.NewTab();
        var toClose = mgr.ActiveTab;
        TabModel? removed = null;
        mgr.TabRemoved += (_, t) => removed = t;

        mgr.RequestCloseActive();

        Assert.Single(mgr.Tabs);
        Assert.Same(toClose, removed);
    }

    [Fact]
    public void RequestCloseActive_on_last_tab_raises_LastTabClosed()
    {
        var mgr = NewManager(out _);
        bool fired = false;
        mgr.LastTabClosed += (_, _) => fired = true;

        mgr.RequestCloseActive();

        Assert.Empty(mgr.Tabs);
        Assert.True(fired);
    }

    [Fact]
    public void Next_wraps_at_end()
    {
        var mgr = NewManager(out _);
        mgr.NewTab(); mgr.NewTab(); // 3 tabs, active = index 2
        mgr.Next();
        Assert.Same(mgr.Tabs[0], mgr.ActiveTab);
    }

    [Fact]
    public void Prev_wraps_at_start()
    {
        var mgr = NewManager(out _);
        mgr.NewTab(); mgr.NewTab();
        mgr.Activate(mgr.Tabs[0]);
        mgr.Prev();
        Assert.Same(mgr.Tabs[2], mgr.ActiveTab);
    }

    [Fact]
    public void JumpTo_out_of_range_is_noop()
    {
        var mgr = NewManager(out _);
        var before = mgr.ActiveTab;
        mgr.JumpTo(5);
        Assert.Same(before, mgr.ActiveTab);
    }

    [Fact]
    public void JumpToLast_with_one_tab_is_noop()
    {
        var mgr = NewManager(out _);
        var before = mgr.ActiveTab;
        mgr.JumpToLast();
        Assert.Same(before, mgr.ActiveTab);
    }

    [Fact]
    public void JumpToLast_with_many_tabs_activates_last()
    {
        var mgr = NewManager(out _);
        mgr.NewTab(); mgr.NewTab();
        mgr.Activate(mgr.Tabs[0]);
        mgr.JumpToLast();
        Assert.Same(mgr.Tabs[2], mgr.ActiveTab);
    }

    [Fact]
    public void Move_reorders_and_raises_TabMoved()
    {
        var mgr = NewManager(out _);
        mgr.NewTab(); mgr.NewTab();
        var t0 = mgr.Tabs[0];
        (TabModel tab, int from, int to)? evt = null;
        mgr.TabMoved += (_, e) => evt = e;

        mgr.Move(0, 2);

        Assert.Same(t0, mgr.Tabs[2]);
        Assert.NotNull(evt);
        Assert.Equal((t0, 0, 2), evt!.Value);
    }

    [Fact]
    public void ActiveTabChanged_fires_on_NewTab_and_Activate()
    {
        var mgr = NewManager(out _);
        int count = 0;
        mgr.ActiveTabChanged += (_, _) => count++;

        mgr.NewTab(); // active becomes the new tab
        mgr.Activate(mgr.Tabs[0]); // active changes back

        Assert.Equal(2, count);
    }

    [Fact]
    public void Activate_to_already_active_tab_does_not_fire()
    {
        var mgr = NewManager(out _);
        mgr.NewTab();
        int count = 0;
        mgr.ActiveTabChanged += (_, _) => count++;
        mgr.Activate(mgr.ActiveTab);
        Assert.Equal(0, count);
    }
}
