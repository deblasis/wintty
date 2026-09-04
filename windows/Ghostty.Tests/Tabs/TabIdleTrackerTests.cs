using System;
using System.Collections.Generic;
using System.Threading;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The idle tracker's contract: a session with no data and no
/// interaction for the window goes idle; arriving data, interaction,
/// activation, or an unacknowledged bell keep it awake. Everything runs
/// on a fake clock so the thresholds are exact, and the marshal is
/// synchronous so the sweep runs where the test can see it.
/// </summary>
public class TabIdleTrackerTests
{
    private sealed class Rig
    {
        public long Now = 5_000;
        public readonly List<FakePaneHost> Hosts;
        public readonly TabManager Manager;
        private TabIdleTracker? _tracker;

        public Rig()
        {
            var hosts = new List<FakePaneHost>();
            Hosts = hosts;
            Manager = new TabManager(_ =>
            {
                var h = new FakePaneHost();
                hosts.Add(h);
                return h;
            });
        }

        public void Start(TimeSpan? idleAfter = null)
        {
            // The timer provider never arms a real timer: tests drive
            // Sweep() themselves, and a live TimeProvider.System timer
            // would keep sweeping this rig from a threadpool thread for
            // the rest of the test-host process.
            _tracker = new TabIdleTracker(
                Manager, a => a(), idleAfter ?? TimeSpan.FromSeconds(10),
                () => Now, new NoTimerProvider());
            _tracker.Start();
        }

        /// <summary>The product's own sweep entry point, driven directly.</summary>
        public void Sweep() => _tracker!.Sweep();
    }

    /// <summary>
    /// A TimeProvider whose CreateTimer hands back a timer that never
    /// fires and cannot be armed -- the null object for the tracker's
    /// periodic sweep in tests.
    /// </summary>
    private sealed class NoTimerProvider : TimeProvider
    {
        private sealed class DeadTimer : ITimer
        {
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new DeadTimer();
    }

    [Fact]
    public void TheActiveTabIsNeverIdle()
    {
        var rig = new Rig();
        rig.Start();
        // Stamp the active tab so the elapsed clause of the sweep rule
        // passes and the active-exemption clause is the only thing left
        // standing between the tab and the moon: without this the test
        // passes vacuously through the zero-stamp fresh rule.
        rig.Manager.Tabs[0].LastActivityTick = rig.Now;

        rig.Now += 60_000;
        rig.Sweep();

        Assert.False(rig.Manager.Tabs[0].IsIdle);
    }

    [Fact]
    public void ABackgroundTabWithoutDataOrInteractionGoesIdle()
    {
        var rig = new Rig();
        rig.Start();
        rig.Manager.NewTab(); // tab 0 is now background; tab 1 is active

        // Below the threshold: awake.
        rig.Now += 9_000;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);

        // Past it: asleep. The switch to the new tab stamped tab 0, so
        // the clock starts there -- 10s after the switch, not after
        // construction.
        rig.Now += 2_000;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);
        Assert.False(rig.Manager.Tabs[1].IsIdle);
    }

    [Fact]
    public void DataOnThePaneWakesAnIdleTab()
    {
        var rig = new Rig();
        rig.Start();
        rig.Manager.NewTab();
        rig.Now += 60_000;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);

        // The pane received data (scrollback grew, a title arrived...):
        // the aggregate stamp moves to "now", and the tab wakes without
        // waiting for the user.
        rig.Hosts[0].LastActivityTick = rig.Now;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);

        // And the clock restarts from that data, not from the old stamp.
        rig.Now += 9_000;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);
        rig.Now += 2_000;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);
    }

    [Fact]
    public void AnUnacknowledgedBellSuppressesIdle()
    {
        var rig = new Rig();
        rig.Start();
        rig.Manager.NewTab();
        rig.Now += 60_000;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);

        rig.Manager.Tabs[0].BellRinging = true;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);

        rig.Manager.Tabs[0].BellRinging = false;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);
    }

    [Fact]
    public void ActivatingAnIdleTabClearsItEagerlyAndStampsBothSides()
    {
        var rig = new Rig();
        rig.Start();
        rig.Manager.NewTab();
        rig.Now += 60_000;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);

        // Going back to the sleeping tab: the moon lifts immediately,
        // and the switch itself counts as interaction on BOTH tabs --
        // the one just left must not dim seconds later.
        rig.Manager.ActivateIndex(0);
        Assert.False(rig.Manager.Tabs[0].IsIdle);

        rig.Now += 9_000;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);
        Assert.False(rig.Manager.Tabs[1].IsIdle);

        rig.Now += 2_000;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);
        Assert.True(rig.Manager.Tabs[1].IsIdle);
    }

    [Fact]
    public void ATabWithNoStampsYetIsNotBornAsleep()
    {
        // Tabs that predate the tracker (a restore seeding the manager)
        // carry no stamps until their first signal: 0 means "fresh",
        // not "infinitely old".
        var rig = new Rig();
        rig.Manager.NewTab();

        rig.Start();
        rig.Now += 3_600_000;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);
    }

    [Fact]
    public void ModelSideStampsCountToo()
    {
        var rig = new Rig();
        rig.Start();
        rig.Manager.NewTab();
        rig.Now += 60_000;
        rig.Sweep();
        Assert.True(rig.Manager.Tabs[0].IsIdle);

        rig.Manager.Tabs[0].LastActivityTick = rig.Now;
        rig.Sweep();
        Assert.False(rig.Manager.Tabs[0].IsIdle);
    }
}
