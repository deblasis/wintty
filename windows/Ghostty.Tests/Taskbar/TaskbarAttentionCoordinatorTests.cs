using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Tests.Tabs;
using Xunit;

namespace Ghostty.Tests.Taskbar;

public class TaskbarAttentionCoordinatorTests
{
    private static (TabManager mgr, List<FakePaneHost> hosts, FakeTaskbarOverlaySink sink, TaskbarAttentionCoordinator coord) New()
    {
        var hosts = new List<FakePaneHost>();
        var mgr = new TabManager(_ =>
        {
            var h = new FakePaneHost();
            hosts.Add(h);
            return h;
        });
        var sink = new FakeTaskbarOverlaySink();
        var coord = new TaskbarAttentionCoordinator(mgr, sink);
        return (mgr, hosts, sink, coord);
    }

    [Fact]
    public void No_writes_on_construction()
    {
        var (_, _, sink, _) = New();
        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Bell_while_unfocused_shows_badge()
    {
        var (_, hosts, sink, coord) = New();
        coord.SetFocused(false);

        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);

        Assert.Equal(new[] { true }, sink.Writes);
    }

    [Fact]
    public void Bell_while_focused_does_nothing()
    {
        var (_, hosts, sink, coord) = New();
        coord.SetFocused(true);

        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Bell_without_attention_feature_shows_no_badge()
    {
        var (_, hosts, sink, coord) = New();
        coord.SetFocused(false);

        // title only, no attention: the badge must stay off even unfocused.
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.TitleOnly);

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Focus_clears_active_badge()
    {
        var (_, hosts, sink, coord) = New();
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);

        coord.SetFocused(true);

        Assert.Equal(new[] { true, false }, sink.Writes);
    }

    [Fact]
    public void Repeated_bells_coalesce_to_one_show()
    {
        var (_, hosts, sink, coord) = New();
        coord.SetFocused(false);

        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);

        Assert.Equal(new[] { true }, sink.Writes);
    }

    [Fact]
    public void Focus_with_no_pending_attention_does_not_clear()
    {
        var (_, _, sink, coord) = New();
        coord.SetFocused(false);
        coord.SetFocused(true);

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Re_arms_after_clear()
    {
        var (_, hosts, sink, coord) = New();
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);   // true
        coord.SetFocused(true);     // false
        coord.SetFocused(false);
        hosts[0].RaiseBellRang(Ghostty.Tests.Bell.BellFixtures.All);   // true

        Assert.Equal(new[] { true, false, true }, sink.Writes);
    }
}
