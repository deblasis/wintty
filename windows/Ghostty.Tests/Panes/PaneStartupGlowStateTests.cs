using System;
using System.Collections.Generic;
using Ghostty.Core.Config;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public class PaneStartupGlowStateTests
{
    // Mirrors the FakeTimer in ConfigWriteSchedulerTests: records
    // scheduling and lets the test fire the callback synchronously.
    private sealed class FakeTimer : ISchedulerTimer
    {
        public Action? Callback { get; set; }
        public TimeSpan? LastScheduled { get; private set; }
        public int ScheduleCount { get; private set; }
        public int CancelCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void Schedule(TimeSpan delay) { LastScheduled = delay; ScheduleCount++; }
        public void Cancel() { CancelCount++; }
        public void Fire() => Callback?.Invoke();
        public void Dispose() { DisposeCount++; }
    }

    private static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(10000);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(250);

    private static (PaneStartupGlowState s, FakeTimer t, List<PaneStartupGlowState.Phase> log) Make()
    {
        var t = new FakeTimer();
        var s = new PaneStartupGlowState(t, Cap, Fade);
        var log = new List<PaneStartupGlowState.Phase>();
        s.StateChanged += p => log.Add(p);
        return (s, t, log);
    }

    [Fact]
    public void Start_enters_glowing_and_arms_cap()
    {
        var (s, t, log) = Make();
        s.Start();
        Assert.Equal(PaneStartupGlowState.Phase.Glowing, s.Current);
        Assert.Equal(Cap, t.LastScheduled);
        Assert.Equal([PaneStartupGlowState.Phase.Glowing], log);
    }

    [Fact]
    public void Cap_elapses_fades_then_idles()
    {
        var (s, t, _) = Make();
        s.Start();
        t.Fire(); // cap elapsed
        Assert.Equal(PaneStartupGlowState.Phase.FadingOut, s.Current);
        t.Fire(); // fade complete
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
    }

    [Fact]
    public void Close_cancels_and_idles()
    {
        var (s, t, _) = Make();
        s.Start();
        s.Close();
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
        Assert.True(t.CancelCount >= 1);
    }

    [Fact]
    public void Start_twice_does_not_rearm()
    {
        var (s, t, log) = Make();
        s.Start();
        s.Start();
        Assert.Equal(1, t.ScheduleCount);
        Assert.Equal([PaneStartupGlowState.Phase.Glowing], log);
    }

    [Fact]
    public void Close_while_fading_out_idles()
    {
        var (s, t, _) = Make();
        s.Start();
        t.Fire(); // -> FadingOut
        s.Close();
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
    }

    [Fact]
    public void NotifyReady_before_cap_fades_then_idles()
    {
        var (s, t, _) = Make();
        s.Start();
        s.NotifyReady(); // ready beat the cap
        Assert.Equal(PaneStartupGlowState.Phase.FadingOut, s.Current);
        Assert.Equal(Fade, t.LastScheduled); // fade timer supersedes the cap
        t.Fire(); // fade complete
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
    }

    [Fact]
    public void NotifyReady_after_fade_started_is_ignored()
    {
        var (s, t, _) = Make();
        s.Start();
        t.Fire(); // cap elapsed -> FadingOut
        var scheduledBefore = t.ScheduleCount;
        s.NotifyReady(); // too late: already fading
        Assert.Equal(PaneStartupGlowState.Phase.FadingOut, s.Current);
        Assert.Equal(scheduledBefore, t.ScheduleCount); // did not re-arm
    }

    [Fact]
    public void NotifyReady_before_start_is_ignored()
    {
        var (s, t, log) = Make();
        s.NotifyReady(); // nothing glowing yet
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
        Assert.Equal(0, t.ScheduleCount);
        Assert.Empty(log);
    }

    [Fact]
    public void Dispose_disposes_timer()
    {
        var t = new FakeTimer();
        var s = new PaneStartupGlowState(t, Cap, Fade);
        s.Dispose();
        Assert.Equal(1, t.DisposeCount);
    }

    [Fact]
    public void Start_after_Dispose_is_a_no_op()
    {
        var (s, t, log) = Make();
        s.Dispose();
        s.Start();
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
        Assert.Equal(0, t.ScheduleCount);
        Assert.Empty(log);
    }

    [Fact]
    public void NotifyReady_after_Dispose_is_a_no_op()
    {
        var (s, t, log) = Make();
        s.Dispose();
        s.NotifyReady();
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
        Assert.Equal(0, t.ScheduleCount);
        Assert.Empty(log);
    }

    [Fact]
    public void Close_after_Dispose_is_a_no_op()
    {
        var (s, t, log) = Make();
        s.Start();
        s.Dispose();
        var cancels = t.CancelCount;
        s.Close();
        Assert.Equal(PaneStartupGlowState.Phase.Idle, s.Current);
        Assert.Equal(cancels, t.CancelCount);
        // Nothing further raised: the Glowing entry is Start's.
        Assert.Equal([PaneStartupGlowState.Phase.Glowing], log);
    }

    [Fact]
    public void Dispose_twice_disposes_the_timer_once()
    {
        var t = new FakeTimer();
        var s = new PaneStartupGlowState(t, Cap, Fade);
        s.Dispose();
        s.Dispose();
        Assert.Equal(1, t.DisposeCount);
        Assert.Equal(1, t.CancelCount);
    }
}
