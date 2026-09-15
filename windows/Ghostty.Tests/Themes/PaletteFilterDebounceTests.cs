using System;
using System.Collections.Generic;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// The palette theme list's filter waits for typing to pause, applies once
/// for the newest text, and never lands after it has been flushed or
/// cancelled.
/// </summary>
public class PaletteFilterDebounceTests
{
    [Fact]
    public void AWordTypedQuicklyAppliesOnceForTheLastText()
    {
        var (debounce, timer) = Make();
        var applied = new List<string>();

        foreach (var text in new[] { "n", "no", "nor", "nord" })
            debounce.Request(() => applied.Add(text));

        Assert.True(debounce.IsPending);
        Assert.Empty(applied);
        Assert.All(timer.Delays, d => Assert.Equal(PaletteFilterDebounce.Delay, d));

        // Every keystroke's callback fires in turn, as a timer that each
        // keystroke restarted would still deliver ones already queued; only
        // the newest may apply.
        timer.RunAll();
        Assert.Equal(new[] { "nord" }, applied);
        Assert.False(debounce.IsPending);
    }

    [Fact]
    public void AnOvertakenWaitNeverAppliesTheNewerTextEarly()
    {
        // The first keystroke's wait ends while the second keystroke's is
        // still running: the second text has not paused yet, so nothing may
        // apply until its own wait ends.
        var (debounce, timer) = Make();
        var applied = new List<string>();
        debounce.Request(() => applied.Add("n"));
        debounce.Request(() => applied.Add("no"));

        timer.RunNext();
        Assert.Empty(applied);
        Assert.True(debounce.IsPending);

        timer.RunNext();
        Assert.Equal(new[] { "no" }, applied);
    }

    [Fact]
    public void TheWaitIsLongEnoughToCoverAWordAndShortEnoughToFollowTyping()
    {
        Assert.InRange(PaletteFilterDebounce.Delay.TotalMilliseconds, 100, 150);
    }

    [Fact]
    public void FlushAppliesNowAndTheQueuedCallbackThenDoesNothing()
    {
        var (debounce, timer) = Make();
        var applied = 0;
        debounce.Request(() => applied++);

        debounce.Flush();
        Assert.Equal(1, applied);
        Assert.False(debounce.IsPending);

        timer.RunAll();
        Assert.Equal(1, applied);
    }

    [Fact]
    public void FlushWithNothingWaitingDoesNothing()
    {
        var (debounce, _) = Make();
        debounce.Flush();
        Assert.False(debounce.IsPending);
    }

    [Fact]
    public void CancelDropsTheWaitingFilter()
    {
        var (debounce, timer) = Make();
        var applied = 0;
        debounce.Request(() => applied++);

        debounce.Cancel();
        Assert.False(debounce.IsPending);
        timer.RunAll();
        Assert.Equal(0, applied);

        // And the next request after a cancel still applies.
        debounce.Request(() => applied++);
        timer.RunAll();
        Assert.Equal(1, applied);
    }

    [Fact]
    public void ARequestAfterAFlushWaitsAgain()
    {
        var (debounce, timer) = Make();
        var applied = new List<string>();
        debounce.Request(() => applied.Add("a"));
        debounce.Flush();

        debounce.Request(() => applied.Add("ab"));
        Assert.True(debounce.IsPending);
        Assert.Equal(new[] { "a" }, applied);

        timer.RunAll();
        Assert.Equal(new[] { "a", "ab" }, applied);
    }

    private static (PaletteFilterDebounce Debounce, Timer Timer) Make()
    {
        var timer = new Timer();
        return (new PaletteFilterDebounce(timer.Schedule), timer);
    }

    private sealed class Timer
    {
        private readonly Queue<Action> _due = new();
        public List<TimeSpan> Delays { get; } = new();

        public void Schedule(TimeSpan delay, Action work)
        {
            Delays.Add(delay);
            _due.Enqueue(work);
        }

        public void RunAll()
        {
            while (_due.Count > 0) _due.Dequeue()();
        }

        public void RunNext() => _due.Dequeue()();
    }
}
