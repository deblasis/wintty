using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Every tab follows its own pane host's title, selected or not.
///
/// The title used to be wired by the window, for one pane only: the
/// selected tab's focused leaf. A background tab froze at whatever it last
/// had while selected (wintty#1129), and a focus change raised by one
/// tab's host was written into whichever tab happened to be selected
/// (wintty#1128). The tab model now subscribes each tab to its own host,
/// the way the directory already was, so neither can happen by
/// construction.
/// </summary>
public class TabTitlePerTabTests
{
    private static TabManager TwoTabs(out FakePaneHost first, out FakePaneHost second)
    {
        var hosts = new System.Collections.Generic.List<FakePaneHost>();
        var mgr = new TabManager(_ =>
        {
            var h = new FakePaneHost();
            hosts.Add(h);
            return h;
        });
        mgr.NewTab();
        first = hosts[0];
        second = hosts[1];
        return mgr;
    }

    [Fact]
    public void ABackgroundTab_FollowsItsShellTitle()
    {
        var mgr = TwoTabs(out var first, out _);
        var background = mgr.Tabs[0];
        mgr.Activate(mgr.Tabs[1]);
        Assert.NotSame(background, mgr.ActiveTab);

        first.RaiseTitleChanged("make -j8");

        Assert.Equal("make -j8", background.ShellReportedTitle);
        Assert.Equal("make -j8", background.EffectiveTitle);
    }

    [Fact]
    public void AHostsTitle_NeverLandsOnTheSelectedTab()
    {
        var mgr = TwoTabs(out var first, out _);
        var selected = mgr.Tabs[1];
        mgr.Activate(selected);
        selected.ShellReportedTitle = "vim notes.md";

        first.RaiseTitleChanged("htop");

        Assert.Equal("vim notes.md", selected.ShellReportedTitle);
        Assert.Equal("htop", mgr.Tabs[0].ShellReportedTitle);
    }

    [Fact]
    public void TheSelectedTab_FollowsItsShellTitle()
    {
        var mgr = TwoTabs(out _, out var second);
        mgr.Activate(mgr.Tabs[1]);

        second.RaiseTitleChanged("ssh prod");

        Assert.Equal("ssh prod", mgr.ActiveTab.ShellReportedTitle);
    }

    [Fact]
    public void ABackgroundTitleChange_DoesNotRetitleTheWindow()
    {
        var mgr = TwoTabs(out var first, out _);
        mgr.Activate(mgr.Tabs[1]);
        var raised = 0;
        mgr.WindowTitleChanged += (_, _) => raised++;

        first.RaiseTitleChanged("background work");

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ClosingATab_TakesItsTitleForwarderOff()
    {
        var mgr = TwoTabs(out var first, out _);
        Assert.Equal(1, first.TitleChangedSubscribers);

        mgr.CloseTab(mgr.Tabs[0]);

        Assert.Equal(0, first.TitleChangedSubscribers);
    }

    [Fact]
    public void AMovedTab_FollowsItsTitleInTheNewWindow_AndOnlyThere()
    {
        var src = TwoTabs(out var first, out _);
        var moving = src.Tabs[0];
        var dst = new TabManager(_ => new FakePaneHost());

        src.DetachTab(moving);
        Assert.Equal(0, first.TitleChangedSubscribers);
        dst.AdoptTab(moving);
        Assert.Equal(1, first.TitleChangedSubscribers);

        first.RaiseTitleChanged("cargo build");

        Assert.Equal("cargo build", moving.ShellReportedTitle);
    }
}
