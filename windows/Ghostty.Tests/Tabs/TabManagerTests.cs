using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerTests
{
    private static TabManager NewManager(out List<FakePaneHost> hosts)
    {
        var hostList = new List<FakePaneHost>();
        var mgr = new TabManager((_) =>
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

    // Regression: PaneHost.LastLeafClosed must drive a tab close. When
    // the shell auto-exits (cmd /c exit under quit-after-last-window-
    // closed), libghostty's close-surface callback raises this event
    // and TabManager owns the bridge into CloseTab. Without the bridge
    // the tab leaks and the window stays open instead of exiting
    // cleanly.
    [Fact]
    public void PaneHost_LastLeafClosed_closes_the_owning_tab()
    {
        var mgr = NewManager(out var hosts);
        mgr.NewTab(); // 2 tabs; close the first via its pane host
        var first = mgr.Tabs[0];
        TabModel? removed = null;
        mgr.TabRemoved += (_, t) => removed = t;

        hosts[0].RaiseLastLeafClosed();

        Assert.Single(mgr.Tabs);
        Assert.Same(first, removed);
    }

    [Fact]
    public void PaneHost_LastLeafClosed_on_only_tab_raises_LastTabClosed()
    {
        var mgr = NewManager(out var hosts);
        bool fired = false;
        mgr.LastTabClosed += (_, _) => fired = true;

        hosts[0].RaiseLastLeafClosed();

        Assert.Empty(mgr.Tabs);
        Assert.True(fired);
    }

    // After DetachTab the source manager must stop reacting to the
    // detached tab's pane-host events: the adopter owns the lifecycle.
    [Fact]
    public void DetachTab_unwires_LastLeafClosed_bridge_on_source()
    {
        var mgr = NewManager(out var hosts);
        mgr.NewTab(); // need >=2 tabs so DetachTab is legal
        var detached = mgr.DetachTab(mgr.Tabs[0]);
        int removedCount = 0;
        mgr.TabRemoved += (_, _) => removedCount++;

        // Fire on the now-orphaned pane host. The source manager must
        // not touch its tab list in response.
        hosts[0].RaiseLastLeafClosed();

        Assert.Equal(0, removedCount);
        Assert.Single(mgr.Tabs);
        Assert.NotNull(detached);
    }

    // The adopter must take over the bridge so a tab moved to a new
    // window still closes its tab when the shell exits.
    [Fact]
    public void AdoptTab_wires_LastLeafClosed_bridge_on_adopter()
    {
        var source = NewManager(out var sourceHosts);
        source.NewTab(); // 2 tabs in source
        var detached = source.DetachTab(source.Tabs[0]);

        var adopter = NewManager(out _);
        adopter.AdoptTab(detached);
        // adopter has its own seed tab plus the adopted one.
        Assert.Equal(2, adopter.Tabs.Count);

        TabModel? removed = null;
        adopter.TabRemoved += (_, t) => removed = t;

        // sourceHosts[0] is the detached tab's pane host; raising
        // LastLeafClosed on it must remove the tab from the adopter.
        sourceHosts[0].RaiseLastLeafClosed();

        Assert.Same(detached, removed);
        Assert.Single(adopter.Tabs);
    }

    [Fact]
    public void BellRang_WithTitleFeature_SetsActiveTabBellRinging()
    {
        var mgr = NewManager(out var hosts);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.TitleOnly);
        Assert.True(mgr.Tabs[0].BellRinging);
    }

    [Fact]
    public void BellRang_WithoutTitleFeature_DoesNotSetBellRinging()
    {
        var mgr = NewManager(out var hosts);
        // attention only, no title: the tab indicator must stay off.
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.AttentionOnly);
        Assert.False(mgr.Tabs[0].BellRinging);
    }

    [Fact]
    public void BellAcknowledged_ClearsBellRinging()
    {
        var mgr = NewManager(out var hosts);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        hosts[0].RaiseBellAcknowledged();
        Assert.False(mgr.Tabs[0].BellRinging);
    }

    [Fact]
    public void BellRang_AfterClose_DoesNotThrowOrLeak()
    {
        var mgr = NewManager(out var hosts);
        mgr.NewTab(); // hosts[1] backs the second tab
        var firstHost = hosts[0];
        mgr.RequestCloseActive(); // closes the active (second) tab
        // The first tab is still open; its host still drives its indicator.
        firstHost.RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        Assert.True(mgr.Tabs[0].BellRinging);
    }
}
