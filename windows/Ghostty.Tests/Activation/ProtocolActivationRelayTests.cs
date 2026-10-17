using System;
using System.Linq;
using Ghostty.Core.Activation;
using Xunit;

namespace Ghostty.Tests.Activation;

public sealed class ProtocolActivationRelayTests
{
    private static readonly Uri Update = new("wintty://update?action=install-restart");

    [Fact]
    public void Note_BeforeAnyoneSubscribes_Latches()
    {
        var relay = new ProtocolActivationRelay();

        relay.Note(Update);

        Assert.Equal(Update, relay.Pending);
    }

    [Fact]
    public void Subscribe_HandsTheLatchToItsFirstSubscriber_AndOnlyTheFirst()
    {
        var relay = new ProtocolActivationRelay();
        relay.Note(Update);

        Uri? first = null;
        relay.Subscribe(uri => first = uri);

        Assert.Equal(Update, first);
        Assert.Null(relay.Pending);

        // The latch is consumed by the first subscriber, so a second one is
        // not a replay of it.
        Uri? second = null;
        relay.Subscribe(uri => second = uri);
        Assert.Null(second);
    }

    [Fact]
    public void Note_WhileSubscribed_DeliversInline_AndDoesNotLatch()
    {
        var relay = new ProtocolActivationRelay();
        Uri? seen = null;
        relay.Subscribe(uri => seen = uri);

        relay.Note(Update);

        Assert.Equal(Update, seen);
        Assert.Null(relay.Pending);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var relay = new ProtocolActivationRelay();
        var calls = 0;
        Action<Uri> handler = _ => calls++;
        relay.Subscribe(handler);

        relay.Unsubscribe(handler);
        relay.Note(Update);

        Assert.Equal(0, calls);
        // With nobody listening again, the URI latches rather than vanishing.
        Assert.Equal(Update, relay.Pending);
    }

    [Fact]
    public void Reset_DropsTheLatch_AndTheHandlers()
    {
        var relay = new ProtocolActivationRelay();
        var calls = 0;
        relay.Subscribe(_ => calls++);
        relay.Note(Update);
        Assert.Equal(1, calls);

        relay.Reset();
        Assert.Null(relay.Pending);

        relay.Note(Update);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotCostTheLaterOnes_ItsUri()
    {
        var relay = new ProtocolActivationRelay(
            ex => Assert.IsType<InvalidOperationException>(ex));
        var later = 0;
        relay.Subscribe(_ => throw new InvalidOperationException());
        relay.Subscribe(_ => later++);

        relay.Note(Update);

        Assert.Equal(1, later);
    }

    // The latch belongs to the first subscriber to arrive, throwing or not:
    // the same load-bearing subscription order the toast relay documents. The
    // throw is reported through the failure sink; the latch is already
    // consumed, so the subscriber behind it sees nothing.
    [Fact]
    public void AThrowingSubscriber_DuringAReplay_StillConsumesTheLatch()
    {
        var relay = new ProtocolActivationRelay(
            ex => Assert.IsType<InvalidOperationException>(ex));
        relay.Note(Update);
        var later = 0;
        relay.Subscribe(_ => throw new InvalidOperationException());
        relay.Subscribe(_ => later++);

        Assert.Equal(0, later);
        Assert.Null(relay.Pending);
    }

    // Subscribing with no handler is the one way to lose a URI outright; the
    // type exists so that never happens by accident, so null is refused.
    [Fact]
    public void Subscribe_RefusesNull()
        => Assert.Throws<ArgumentNullException>(() => new ProtocolActivationRelay().Subscribe(null!));

    [Fact]
    public void Pending_IsNull_WhenNothingWasEverNoted()
        => Assert.Null(new ProtocolActivationRelay().Pending);
}
