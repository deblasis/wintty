using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Closing a tab above the active one is the case the vertical strip drew
/// wrong: the surviving tab keeps its identity but moves down a slot, and
/// the strip went on painting the selected row where that tab used to be.
///
/// This is the model half of the invariant a strip has to follow. It is
/// deliberately about identity rather than index, because index is the
/// thing that changes: a strip that tracks the active tab by ordinal is
/// correct here and wrong on screen.
/// </summary>
public class TabCloseSelectionIdentityTests
{
    private static TabManager NewManager()
        => new(_ => new FakePaneHost());

    [Fact]
    public void CloseTab_above_active_keeps_the_same_tab_active()
    {
        var mgr = NewManager();
        mgr.NewTab(); // 2 tabs, active = index 1
        var active = mgr.ActiveTab;

        mgr.CloseTab(mgr.Tabs[0]);

        Assert.Single(mgr.Tabs);
        Assert.Same(active, mgr.ActiveTab);
        Assert.Same(active, mgr.Tabs[0]);
    }

    /// <summary>
    /// The active tab did not change, so nothing announces one. The strip
    /// therefore cannot lean on <c>ActiveTabChanged</c> to re-place its
    /// selection row after this close; the collection change is the only
    /// signal it gets, and it has to stand on its own.
    /// </summary>
    [Fact]
    public void CloseTab_above_active_announces_no_activation()
    {
        var mgr = NewManager();
        mgr.NewTab();
        var announced = new List<TabModel>();
        mgr.ActiveTabChanged += (_, t) => announced.Add(t);

        mgr.CloseTab(mgr.Tabs[0]);

        Assert.Empty(announced);
    }

    [Fact]
    public void CloseTab_above_active_shifts_the_active_index_down()
    {
        var mgr = NewManager();
        mgr.NewTab();
        mgr.NewTab(); // 3 tabs, active = index 2
        var active = mgr.ActiveTab;

        mgr.CloseTab(mgr.Tabs[0]);

        Assert.Same(active, mgr.ActiveTab);
        Assert.Equal(1, mgr.IndexOf(active));
    }

    [Fact]
    public void CloseTab_below_active_leaves_the_active_index_alone()
    {
        var mgr = NewManager();
        mgr.NewTab();
        mgr.NewTab();
        mgr.Activate(mgr.Tabs[0]);
        var active = mgr.ActiveTab;

        mgr.CloseTab(mgr.Tabs[2]);

        Assert.Same(active, mgr.ActiveTab);
        Assert.Equal(0, mgr.IndexOf(active));
    }

    /// <summary>
    /// Repeated closes from the top with the last tab active: every one of
    /// them moves the active row, and the strip has to re-place its
    /// selection fill each time rather than only on the first.
    /// </summary>
    [Fact]
    public void Closing_every_tab_above_active_keeps_identity_throughout()
    {
        var mgr = NewManager();
        for (int i = 0; i < 4; i++) mgr.NewTab();
        var active = mgr.ActiveTab;

        while (mgr.Tabs.Count > 1)
        {
            mgr.CloseTab(mgr.Tabs[0]);
            Assert.Same(active, mgr.ActiveTab);
            Assert.Equal(mgr.Tabs.Count - 1, mgr.IndexOf(active));
        }
    }
}
