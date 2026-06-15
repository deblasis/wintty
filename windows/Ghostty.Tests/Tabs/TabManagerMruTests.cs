using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerMruTests
{
    private static TabManager NewManager()
        => new TabManager((_) => new FakePaneHost());

    [Fact]
    public void Initial_tab_is_in_mru()
    {
        var mgr = NewManager();
        Assert.Single(mgr.MruOrder);
        Assert.Same(mgr.ActiveTab, mgr.MruOrder[0]);
    }

    [Fact]
    public void NewTab_becomes_most_recent_previous_active_second()
    {
        var mgr = NewManager();
        var first = mgr.ActiveTab;
        var second = mgr.NewTab();
        Assert.Same(second, mgr.MruOrder[0]);
        Assert.Same(first, mgr.MruOrder[1]);
    }

    [Fact]
    public void Activating_an_older_tab_moves_it_to_front()
    {
        var mgr = NewManager();
        var first = mgr.ActiveTab;
        mgr.NewTab();          // second active
        mgr.NewTab();          // third active
        mgr.Activate(first);
        Assert.Same(first, mgr.MruOrder[0]);
    }

    [Fact]
    public void Closing_active_removes_it_and_new_active_is_front()
    {
        var mgr = NewManager();
        mgr.NewTab();          // second active
        var second = mgr.ActiveTab;
        mgr.CloseTab(second);
        Assert.DoesNotContain(second, mgr.MruOrder);
        Assert.Same(mgr.ActiveTab, mgr.MruOrder[0]);
    }
}
