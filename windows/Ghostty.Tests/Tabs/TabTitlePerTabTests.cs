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

    /// <summary>
    /// Torn off twice, A to B to C: the adopter's forwarder has to come off
    /// too, or B's handler stays on the host and keeps B's manager (and its
    /// window) reachable after B is gone.
    /// </summary>
    [Fact]
    public void ATabMovedTwice_LeavesOneForwarder_AndNoneOnceClosed()
    {
        var a = TwoTabs(out var first, out _);
        var moving = a.Tabs[0];
        var b = new TabManager(_ => new FakePaneHost());
        var c = new TabManager(_ => new FakePaneHost());

        a.DetachTab(moving);
        b.AdoptTab(moving);
        b.DetachTab(moving);
        Assert.Equal(0, first.TitleChangedSubscribers);
        c.AdoptTab(moving);
        Assert.Equal(1, first.TitleChangedSubscribers);

        first.RaiseTitleChanged("third window");
        Assert.Equal("third window", moving.ShellReportedTitle);

        c.CloseTab(moving);
        Assert.Equal(0, first.TitleChangedSubscribers);
    }

    /// <summary>
    /// A background tab now hears its shell, which a rename has to survive:
    /// the user's name stays on top while the shell's titles keep updating
    /// underneath, and clearing the name brings back the live title.
    /// </summary>
    [Fact]
    public void ARenamedBackgroundTab_StaysRenamed_WhileItsShellSetsTitles()
    {
        var mgr = TwoTabs(out var first, out _);
        var renamed = mgr.Tabs[0];
        renamed.UserOverrideTitle = "prod";
        mgr.Activate(mgr.Tabs[1]);

        first.RaiseTitleChanged("vim a.txt");
        first.RaiseTitleChanged("bash");

        Assert.Equal("prod", renamed.EffectiveTitle);
        Assert.Equal("prod", renamed.WordTitle);
        Assert.Equal("bash", renamed.ShellReportedTitle);

        renamed.UserOverrideTitle = null;
        Assert.Equal("bash", renamed.EffectiveTitle);
    }

    /// <summary>
    /// The founder's ruling, as Windows Terminal does it: when focus moves to
    /// a pane that has not reported a title, the host sends null and the tab
    /// shows its fallback, not the previous pane's title.
    /// </summary>
    [Fact]
    public void AFocusedUntitledPane_ShowsTheFallback_NotThePreviousTitle()
    {
        var mgr = TwoTabs(out var first, out _);
        var tab = mgr.Tabs[0];
        var fallback = tab.EffectiveTitle;
        first.RaiseTitleChanged("ssh prod");
        Assert.Equal("ssh prod", tab.EffectiveTitle);

        // What PaneHost sends when focus lands on a fresh split.
        first.RaiseCwdChanged(null);
        first.RaiseTitleChanged(null);

        Assert.Null(tab.ShellReportedTitle);
        Assert.Equal(fallback, tab.EffectiveTitle);
        Assert.NotEqual("ssh prod", fallback);
    }
}
