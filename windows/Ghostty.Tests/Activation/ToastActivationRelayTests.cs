using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Activation;
using Xunit;

namespace Ghostty.Tests.Activation;

public sealed class ToastActivationRelayTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortEnoughToFailFast = TimeSpan.FromSeconds(2);

    [Fact]
    public void Note_ThenSubscribe_ReplaysToTheSubscriber()
    {
        var relay = new ToastActivationRelay();
        relay.Note(new ToastActivation("abc"));

        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        Assert.Equal("abc", Assert.Single(seen).SurfaceKey);
    }

    [Fact]
    public void Subscribe_ThenNote_DeliversLive()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        relay.Note(new ToastActivation("abc"));

        Assert.Equal("abc", Assert.Single(seen).SurfaceKey);
    }

    // The latch belongs to whoever gets there first. A second subscriber
    // arriving later must not see a click that was already handed over, or a
    // cold-launch activation would be acted on twice.
    [Fact]
    public void Replay_GoesToTheFirstSubscriberOnly()
    {
        var relay = new ToastActivationRelay();
        relay.Note(new ToastActivation("abc"));

        var first = new List<ToastActivation>();
        var second = new List<ToastActivation>();
        relay.Subscribe(first.Add);
        relay.Subscribe(second.Add);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void Replay_ConsumesTheLatch()
    {
        var relay = new ToastActivationRelay();
        relay.Note(new ToastActivation("abc"));
        Assert.Equal("abc", relay.Pending.SurfaceKey);

        relay.Subscribe(_ => { });

        Assert.False(relay.Pending.HasSurface);
    }

    // Pending is a read, not a consume: the forwarding path reads it and then
    // exits the process without ever subscribing.
    [Fact]
    public void Pending_DoesNotConsumeTheLatch()
    {
        var relay = new ToastActivationRelay();
        relay.Note(new ToastActivation("abc"));

        Assert.Equal("abc", relay.Pending.SurfaceKey);
        Assert.Equal("abc", relay.Pending.SurfaceKey);
    }

    [Fact]
    public void Note_WithSubscribers_DoesNotLatch()
    {
        var relay = new ToastActivationRelay();
        relay.Subscribe(_ => { });

        relay.Note(new ToastActivation("abc"));

        Assert.False(relay.Pending.HasSurface);
    }

    [Fact]
    public void Note_ReachesEverySubscriber()
    {
        var relay = new ToastActivationRelay();
        var first = new List<ToastActivation>();
        var second = new List<ToastActivation>();
        relay.Subscribe(first.Add);
        relay.Subscribe(second.Add);

        relay.Note(new ToastActivation("abc"));

        Assert.Single(first);
        Assert.Single(second);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        void Handler(ToastActivation a) => seen.Add(a);

        relay.Subscribe(Handler);
        relay.Unsubscribe(Handler);
        relay.Note(new ToastActivation("abc"));

        Assert.Empty(seen);
        // Nobody is listening any more, so the click latches rather than
        // vanishing.
        Assert.Equal("abc", relay.Pending.SurfaceKey);
    }

    // Teardown: the relay is process-lifetime and outlives what subscribes to
    // it, so a handler left attached roots its owner for the life of the
    // process.
    [Fact]
    public void Reset_DropsHandlers()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        relay.Reset();
        relay.Note(new ToastActivation("abc"));

        Assert.Empty(seen);
    }

    [Fact]
    public void Reset_DropsTheLatch()
    {
        var relay = new ToastActivationRelay();
        relay.Note(new ToastActivation("abc"));

        relay.Reset();

        Assert.False(relay.Pending.HasSurface);
    }

    // A throwing handler on the replay path runs inline on the startup thread.
    // Left unguarded it would turn a toast-driven cold launch into a silent
    // launch failure.
    [Fact]
    public void ThrowingHandler_IsReportedNotPropagated_OnReplay()
    {
        var failures = new List<Exception>();
        var relay = new ToastActivationRelay(failures.Add);
        relay.Note(new ToastActivation("abc"));

        relay.Subscribe(_ => throw new InvalidOperationException("boom"));

        Assert.Equal("boom", Assert.Single(failures).Message);
    }

    [Fact]
    public void ThrowingHandler_IsReportedNotPropagated_OnNote()
    {
        var failures = new List<Exception>();
        var relay = new ToastActivationRelay(failures.Add);
        relay.Subscribe(_ => throw new InvalidOperationException("boom"));

        relay.Note(new ToastActivation("abc"));

        Assert.Equal("boom", Assert.Single(failures).Message);
    }

    // The intersection nothing else covered: Note_ReachesEverySubscriber uses
    // only well-behaved handlers, and ThrowingHandler_..._OnNote uses only one
    // subscriber. Invoking the multicast delegate as a single call propagates
    // the first exception immediately, so every target behind the thrower is
    // silently skipped.
    [Fact]
    public void Note_ThrowingFirstHandler_StillReachesTheSecond()
    {
        var failures = new List<Exception>();
        var relay = new ToastActivationRelay(failures.Add);
        var second = new List<ToastActivation>();

        relay.Subscribe(_ => throw new InvalidOperationException("boom"));
        relay.Subscribe(second.Add);

        relay.Note(new ToastActivation("abc"));

        Assert.Equal("abc", Assert.Single(second).SurfaceKey);
        Assert.Single(failures);
    }

    // The failure sink is caller-supplied and is called from inside a catch.
    // A throw from it would escape the very call the guard exists to protect.
    [Fact]
    public void ThrowingFailureSink_DoesNotEscapeNote()
    {
        var relay = new ToastActivationRelay(_ => throw new InvalidOperationException("sink"));
        relay.Subscribe(_ => throw new InvalidOperationException("boom"));

        relay.Note(new ToastActivation("abc"));
    }

    // The launch click can arrive twice, from the activation arguments and
    // from the notification callback, describing the same click. Only the
    // first record may act.
    [Fact]
    public void TryNoteLaunchActivation_OnlyTheFirstCallDelivers()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("abc")));
        Assert.False(relay.TryNoteLaunchActivation(new ToastActivation("abc")));

        Assert.Equal("abc", Assert.Single(seen).SurfaceKey);
    }

    // Deduping the launch click must not dedupe the warm ones behind it.
    [Fact]
    public void TryNoteLaunchActivation_DoesNotSuppressLaterWarmClicks()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        relay.TryNoteLaunchActivation(new ToastActivation("launch"));
        relay.Note(new ToastActivation("warm"));

        Assert.Equal(["launch", "warm"], seen.Select(a => a.SurfaceKey));
    }

    [Fact]
    public void TryNoteLaunchActivation_LatchesWhenNobodyIsListening()
    {
        var relay = new ToastActivationRelay();

        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("abc")));

        Assert.Equal("abc", relay.Pending.SurfaceKey);
    }

    // The shell may hand the launch click over ONE way only, in which case no
    // second call ever arrives -- and the next call is a person clicking a new
    // toast. Deduping on ordinal position instead of identity swallowed it:
    // the click did nothing at all, not even bring the app forward.
    [Fact]
    public void TryNoteLaunchActivation_ADifferentActivationIsNotADuplicate()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("launch")));
        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("clicked-later")));

        Assert.Equal(["launch", "clicked-later"], seen.Select(a => a.SurfaceKey));
    }

    // The remaining ambiguity: a real click on the SAME surface the launch
    // click named. Indistinguishable from a redelivery until startup declares
    // itself over.
    [Fact]
    public void TryNoteLaunchActivation_SameActivationIsADuplicateUntilTheWindowCloses()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);

        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("abc")));
        Assert.False(relay.TryNoteLaunchActivation(new ToastActivation("abc")));

        relay.CloseLaunchWindow();
        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("abc")));

        Assert.Equal(["abc", "abc"], seen.Select(a => a.SurfaceKey));
    }

    [Fact]
    public void CloseLaunchWindow_LetsAnyLaterClickThrough()
    {
        var relay = new ToastActivationRelay();
        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);
        relay.TryNoteLaunchActivation(new ToastActivation("abc"));
        relay.CloseLaunchWindow();

        relay.TryNoteLaunchActivation(new ToastActivation("abc"));
        relay.TryNoteLaunchActivation(new ToastActivation("abc"));

        Assert.Equal(3, seen.Count);
    }

    // Reset exists to make the relay reusable, so it has to restore the WHOLE
    // launch gate. Leaving half of it set (the record cleared, the window
    // closed) is how a second startup delivers the launch click twice.
    [Fact]
    public void Reset_RestoresTheWholeLaunchGate()
    {
        var relay = new ToastActivationRelay();
        relay.TryNoteLaunchActivation(new ToastActivation("abc"));
        relay.CloseLaunchWindow();

        relay.Reset();

        var seen = new List<ToastActivation>();
        relay.Subscribe(seen.Add);
        Assert.True(relay.TryNoteLaunchActivation(new ToastActivation("abc")));
        Assert.False(relay.TryNoteLaunchActivation(new ToastActivation("abc")));

        Assert.Single(seen);
    }

    // Handlers activate windows and can re-enter the relay. Holding the gate
    // across the call would let one slow handler block every other thread, and
    // Note runs on a WinRT callback thread while Pending is read from the
    // forwarding path.
    [Fact]
    public void Note_DoesNotHoldTheLockWhileInvokingHandlers()
    {
        var relay = new ToastActivationRelay();
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        relay.Subscribe(_ =>
        {
            inHandler.Set();
            release.Wait(Generous);
        });

        var noting = Task.Run(() => relay.Note(new ToastActivation("abc")));
        Assert.True(inHandler.Wait(Generous), "handler never ran");

        try
        {
            var probe = Task.Run(() => relay.Pending);
            Assert.True(
                probe.Wait(ShortEnoughToFailFast),
                "Pending blocked while a handler was running: the gate is held across invocation");
        }
        finally
        {
            release.Set();
            noting.Wait(Generous);
        }
    }

    [Fact]
    public void Subscribe_DoesNotHoldTheLockWhileReplaying()
    {
        var relay = new ToastActivationRelay();
        relay.Note(new ToastActivation("abc"));

        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var subscribing = Task.Run(() => relay.Subscribe(_ =>
        {
            inHandler.Set();
            release.Wait(Generous);
        }));
        Assert.True(inHandler.Wait(Generous), "replay never ran");

        try
        {
            var probe = Task.Run(() => relay.Pending);
            Assert.True(
                probe.Wait(ShortEnoughToFailFast),
                "Pending blocked while a replay was running: the gate is held across invocation");
        }
        finally
        {
            release.Set();
            subscribing.Wait(Generous);
        }
    }
}
