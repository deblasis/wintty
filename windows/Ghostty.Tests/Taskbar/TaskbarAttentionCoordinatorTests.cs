using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Notifications;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Tests.Tabs;
using Xunit;

namespace Ghostty.Tests.Taskbar;

public class TaskbarAttentionCoordinatorTests
{
    private static (TabManager mgr, List<FakePaneHost> hosts, FakeTaskbarBadgeSink sink, NotificationService notices, TaskbarAttentionCoordinator coord) New()
    {
        var hosts = new List<FakePaneHost>();
        var mgr = new TabManager(_ =>
        {
            var h = new FakePaneHost();
            hosts.Add(h);
            return h;
        });
        var sink = new FakeTaskbarBadgeSink();
        var notices = new NotificationService();
        var coord = new TaskbarAttentionCoordinator(mgr, new TaskbarBadgeArbiter(sink, notices));
        return (mgr, hosts, sink, notices, coord);
    }

    [Fact]
    public void No_writes_on_construction()
    {
        var (_, _, sink, _, _) = New();
        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Bell_while_unfocused_shows_badge_and_notice()
    {
        var (_, hosts, sink, notices, coord) = New();
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        Assert.Equal(TaskbarBadgeKind.Bell, sink.Current);
        // Namespaced by a per-arbiter scope, so match the shape rather than
        // the literal: two windows must not share a key, or the second
        // window's notice is deduped away and its dot has nothing behind it.
        var key = Assert.Single(notices.Active).DedupKey;
        Assert.StartsWith("badge:", key);
        Assert.EndsWith(":bell", key);
    }

    [Fact]
    public void Bell_while_focused_does_nothing()
    {
        var (_, hosts, sink, _, coord) = New();
        coord.SetFocused(true);

        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Bell_without_attention_feature_shows_no_badge()
    {
        var (_, hosts, sink, _, coord) = New();
        coord.SetFocused(false);

        // title only, no attention: the badge must stay off even unfocused.
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.TitleOnly);

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Focus_clears_the_badge_but_keeps_the_notice()
    {
        var (_, hosts, sink, notices, coord) = New();
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        coord.SetFocused(true);
        Assert.Null(sink.Current);
        Assert.Single(notices.Active);
    }

    [Fact]
    public void Two_bells_in_one_episode_write_once()
    {
        var (_, hosts, sink, _, coord) = New();
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        Assert.Single(sink.Writes);
    }

    [Fact]
    public void Focus_with_no_pending_attention_does_not_clear()
    {
        var (_, _, sink, _, coord) = New();
        coord.SetFocused(false);
        coord.SetFocused(true);

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void A_later_episode_badges_again()
    {
        var (_, hosts, sink, notices, coord) = New();
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        coord.SetFocused(true);
        notices.Dismiss(notices.Active.Single());
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        Assert.Equal(TaskbarBadgeKind.Bell, sink.Current);
    }
}
