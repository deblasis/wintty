using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The window caption follows the selected tab's label, from the moment the
/// window is built. TitleBarCoordinator hands the caption to this class, so
/// these are the caption's behaviour tests.
/// </summary>
public class WindowTitleFollowerTests
{
    private static TabManager TwoTabs(out FakePaneHost first, out FakePaneHost second)
    {
        var hosts = new List<FakePaneHost>();
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

    /// <summary>
    /// Nothing raises WindowTitleChanged while the window is being built, so
    /// without the write at construction the caption stays empty until the
    /// first tab switch or title.
    /// </summary>
    [Fact]
    public void TheCaption_IsSetAtConstruction()
    {
        var mgr = TwoTabs(out var first, out _);
        first.RaiseTitleChanged("make -j8");
        mgr.Activate(mgr.Tabs[0]);
        var captions = new List<string>();

        new WindowTitleFollower(mgr, captions.Add);

        Assert.Equal(new[] { "make -j8" }, captions);
    }

    [Fact]
    public void TheCaption_FollowsTheSelectedTab_AndItsTitle()
    {
        var mgr = TwoTabs(out var first, out var second);
        first.RaiseTitleChanged("one");
        second.RaiseTitleChanged("two");
        mgr.Activate(mgr.Tabs[0]);
        var captions = new List<string>();
        new WindowTitleFollower(mgr, captions.Add);

        mgr.Activate(mgr.Tabs[1]);
        second.RaiseTitleChanged("two again");

        Assert.Equal("two again", captions[^1]);
        Assert.Contains("two", captions);
    }

    [Fact]
    public void ABackgroundTabsTitle_NeverReachesTheCaption()
    {
        var mgr = TwoTabs(out var first, out var second);
        second.RaiseTitleChanged("selected");
        mgr.Activate(mgr.Tabs[1]);
        var captions = new List<string>();
        new WindowTitleFollower(mgr, captions.Add);

        first.RaiseTitleChanged("background work");

        Assert.DoesNotContain("background work", captions);
        Assert.Equal("selected", captions[^1]);
    }
}
