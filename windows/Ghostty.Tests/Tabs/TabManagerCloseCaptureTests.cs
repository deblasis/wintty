using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Session;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerCloseCaptureTests
{
    private static TabManager NewManager(
        ClosedStack<TabSession> closed,
        out List<FakePaneHost> hosts)
    {
        var list = new List<FakePaneHost>();
        var mgr = new TabManager(
            paneHostFactory: _ =>
            {
                var h = new FakePaneHost();
                list.Add(h);
                return h;
            },
            closedTabs: closed);
        hosts = list;
        return mgr;
    }

    [Fact]
    public void Closing_a_tab_pushes_a_session_snapshot()
    {
        var closed = new ClosedStack<TabSession>(25);
        var mgr = NewManager(closed, out _);
        mgr.NewTab(); // 2 tabs

        mgr.CloseTab(mgr.Tabs[1]);

        Assert.Equal(1, closed.Count);
        Assert.True(closed.TryPop(out var snap));
        Assert.NotNull(snap.Tree); // a real captured tree
    }

    [Fact]
    public void Closing_the_last_tab_also_captures_it()
    {
        var closed = new ClosedStack<TabSession>(25);
        var mgr = NewManager(closed, out _);

        mgr.CloseTab(mgr.Tabs[0]); // last tab -> window would close

        Assert.Equal(1, closed.Count);
    }

    [Fact]
    public void Capture_records_user_title_and_disposes_the_tab()
    {
        var closed = new ClosedStack<TabSession>(25);
        var mgr = NewManager(closed, out var hosts);
        mgr.NewTab();
        mgr.Tabs[1].UserOverrideTitle = "kept";

        mgr.CloseTab(mgr.Tabs[1]);

        Assert.True(closed.TryPop(out var snap));
        Assert.Equal("kept", snap.UserTitle);
        // The close path is otherwise unchanged: the tab is still disposed.
        Assert.Equal(1, hosts[1].DisposeAllCalls);
    }

    [Fact]
    public void Manager_without_a_stack_still_closes_normally()
    {
        var list = new List<FakePaneHost>();
        var mgr = new TabManager(_ =>
        {
            var h = new FakePaneHost();
            list.Add(h);
            return h;
        });
        mgr.NewTab();

        mgr.CloseTab(mgr.Tabs[1]); // no closedTabs injected -> no throw

        Assert.Single(mgr.Tabs);
    }
}
