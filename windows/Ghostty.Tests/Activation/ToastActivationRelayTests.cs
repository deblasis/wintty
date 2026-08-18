using System;
using System.Collections.Generic;
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
