using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Tests.Tabs;
using Xunit;

namespace Ghostty.Tests.Taskbar;

public class TaskbarProgressCoordinatorTests
{
    private static (TabManager mgr, List<FakePaneHost> hosts) NewManager()
    {
        var hostList = new List<FakePaneHost>();
        var mgr = new TabManager(() =>
        {
            var h = new FakePaneHost();
            hostList.Add(h);
            return h;
        });
        return (mgr, hostList);
    }

    /// <summary>
    /// Test-controllable clock. Tests advance time manually and then
    /// call Tick(); the real implementation uses DateTime.UtcNow and
    /// a DispatcherQueueTimer.
    /// </summary>
    private sealed class TestClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan t) => Now += t;
    }

    [Fact]
    public void Clear_on_construction()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        // No progress reported yet.
        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void First_progress_goes_to_Single_live()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(42);

        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(42), sink.Last);
    }

    [Fact]
    public void Second_tab_progress_starts_Cycling()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        mgr.NewTab();
        sink.Reset();
        mgr.Tabs[1].Progress = TabProgressState.Normal(60);

        // On entering Cycling, the coordinator emits the currently
        // displayed tab's state (tab 0, the one that was already
        // showing). Subsequent ticks advance to the newly added tab.
        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(30), sink.Last);
    }

    [Fact]
    public void Cycling_tick_advances_to_next_tab()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        mgr.NewTab();
        mgr.Tabs[1].Progress = TabProgressState.Normal(60);
        sink.Reset();

        clock.Advance(TimeSpan.FromSeconds(2));
        coord.Tick();

        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(60), sink.Last);
    }

    [Fact]
    public void Live_update_to_current_slot_passes_through()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        mgr.NewTab();
        mgr.Tabs[1].Progress = TabProgressState.Normal(60);
        sink.Reset();

        // Still showing tab 0 as the cycling index. Live update to
        // tab 0 should pass through; update to tab 1 should not.
        mgr.Tabs[0].Progress = TabProgressState.Normal(40);
        mgr.Tabs[1].Progress = TabProgressState.Normal(70);

        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(40), sink.Last);
    }

    [Fact]
    public void Tab_clears_progress_while_cycling_falls_back_to_single()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        mgr.NewTab();
        mgr.Tabs[1].Progress = TabProgressState.Normal(60);
        sink.Reset();

        // Tab 1 clears. Falls back to Single(tab0), writes tab 0's state.
        mgr.Tabs[1].Progress = TabProgressState.None;

        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(30), sink.Last);
    }

    [Fact]
    public void All_tabs_clear_emits_None()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        sink.Reset();
        mgr.Tabs[0].Progress = TabProgressState.None;

        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.None, sink.Last);
    }

    [Fact]
    public void Tab_removed_while_cycling_drops_from_active_list()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        mgr.NewTab();
        mgr.Tabs[1].Progress = TabProgressState.Normal(60);
        sink.Reset();

        // Close the non-current tab mid-cycle — state machine should
        // fall back to Single(tab0) and emit tab 0's state.
        mgr.CloseTab(mgr.Tabs[1]);

        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(30), sink.Last);

        // Further ticks must not revisit the removed tab.
        clock.Advance(TimeSpan.FromSeconds(10));
        coord.Tick();
        Assert.Single(sink.Writes);
    }

    [Fact]
    public void Dispose_unsubscribes_from_manager_and_tabs()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        coord.Dispose();

        // After dispose, progress changes must not reach the sink.
        mgr.Tabs[0].Progress = TabProgressState.Normal(42);
        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void Pause_freezes_ticks_resume_restarts_slot()
    {
        var (mgr, _) = NewManager();
        var sink = new FakeTaskbarProgressSink();
        var clock = new TestClock();
        var coord = new TaskbarProgressCoordinator(mgr, sink, () => clock.Now);

        mgr.Tabs[0].Progress = TabProgressState.Normal(30);
        mgr.NewTab();
        mgr.Tabs[1].Progress = TabProgressState.Normal(60);
        sink.Reset();

        coord.Pause();

        // While paused, ticks are ignored.
        clock.Advance(TimeSpan.FromSeconds(10));
        coord.Tick();
        Assert.Empty(sink.Writes);

        coord.Resume();
        // Resume starts a fresh slot at the current index (still 0).
        // Nothing written yet until the next Tick.
        Assert.Empty(sink.Writes);

        clock.Advance(TimeSpan.FromSeconds(2));
        coord.Tick();
        Assert.Single(sink.Writes);
        Assert.Equal(TabProgressState.Normal(60), sink.Last); // advanced to tab 1
    }
}
