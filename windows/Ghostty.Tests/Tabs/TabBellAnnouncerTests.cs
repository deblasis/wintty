using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// ItemStatus is the right property for a ringing tab and no shipping
/// screen reader reads it off a tab or a list item, on focus or on
/// change. So the ring also goes out as an announcement, and everything
/// about when that fires lives here.
/// </summary>
public class TabBellAnnouncerTests
{
    private static (TabManager mgr, List<string> spoken, TabBellAnnouncer announcer) NewAnnouncer()
    {
        var mgr = new TabManager(_ => new FakePaneHost());
        var spoken = new List<string>();
        return (mgr, spoken, new TabBellAnnouncer(mgr, (_, text) => spoken.Add(text)));
    }

    [Fact]
    public void Ringing_tab_is_announced_with_its_title()
    {
        var (mgr, spoken, _) = NewAnnouncer();
        mgr.Tabs[0].ShellReportedTitle = "vim file.txt";

        mgr.Tabs[0].BellRinging = true;

        Assert.Equal(new[] { "Bell in vim file.txt" }, spoken);
    }

    /// <summary>
    /// A shell that rings twice while the tab is still ringing has told
    /// the user nothing new. BellRinging guards its setter on equality,
    /// so the second write raises no change and this stays silent.
    /// </summary>
    [Fact]
    public void Second_ring_while_still_ringing_says_nothing()
    {
        var (mgr, spoken, _) = NewAnnouncer();

        mgr.Tabs[0].BellRinging = true;
        mgr.Tabs[0].BellRinging = true;

        Assert.Single(spoken);
    }

    [Fact]
    public void Acknowledging_the_bell_says_nothing()
    {
        var (mgr, spoken, _) = NewAnnouncer();
        mgr.Tabs[0].BellRinging = true;
        spoken.Clear();

        mgr.Tabs[0].BellRinging = false;

        Assert.Empty(spoken);
    }

    [Fact]
    public void Ringing_again_after_an_acknowledge_is_announced_again()
    {
        var (mgr, spoken, _) = NewAnnouncer();

        mgr.Tabs[0].BellRinging = true;
        mgr.Tabs[0].BellRinging = false;
        mgr.Tabs[0].BellRinging = true;

        Assert.Equal(2, spoken.Count);
    }

    /// <summary>
    /// Unrelated changes on the tab are not bells. A rename raises
    /// PropertyChanged too, and announcing on it would speak a bell every
    /// time the shell emitted OSC 2.
    /// </summary>
    [Fact]
    public void Other_property_changes_say_nothing()
    {
        var (mgr, spoken, _) = NewAnnouncer();

        mgr.Tabs[0].ShellReportedTitle = "vim file.txt";
        mgr.Tabs[0].Color = TabColor.Red;

        Assert.Empty(spoken);
    }

    /// <summary>
    /// The whole point is a tab the user is not on, and tabs are opened
    /// long after the window is built, so a tab that arrives later has to
    /// be watched too.
    /// </summary>
    [Fact]
    public void Tab_added_after_construction_is_announced()
    {
        var (mgr, spoken, _) = NewAnnouncer();
        mgr.NewTab();
        mgr.Tabs[1].ShellReportedTitle = "pwsh";

        mgr.Tabs[1].BellRinging = true;

        Assert.Equal(new[] { "Bell in pwsh" }, spoken);
    }

    [Fact]
    public void Closed_tab_is_no_longer_announced()
    {
        var (mgr, spoken, _) = NewAnnouncer();
        mgr.NewTab();
        var closed = mgr.Tabs[1];

        mgr.CloseTab(closed);
        closed.BellRinging = true;

        Assert.Empty(spoken);
    }

    [Fact]
    public void Disposed_announcer_says_nothing()
    {
        var (mgr, spoken, announcer) = NewAnnouncer();

        announcer.Dispose();
        mgr.Tabs[0].BellRinging = true;

        Assert.Empty(spoken);
    }

    /// <summary>
    /// An untitled tab still has to name something a listener can hear:
    /// the announcement runs through the same fallback the tab's name
    /// does, so the two agree.
    /// </summary>
    [Fact]
    public void Untitled_tab_is_announced_by_its_fallback_name()
    {
        var (mgr, spoken, _) = NewAnnouncer();
        mgr.Tabs[0].ShellReportedTitle = "   ";

        mgr.Tabs[0].BellRinging = true;

        Assert.Equal(new[] { $"Bell in {Ghostty.Core.AppIdentity.ProductName}" }, spoken);
    }
}
