using System;
using System.Collections.Generic;
using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

public sealed class LaunchDeferralQueueTests
{
    private static LaunchRequest Req(string cwd = @"C:\tmp", params string[] args)
        => new(cwd, args);

    [Fact]
    public void EmptyQueue_DrainsNothing()
    {
        var queue = new LaunchDeferralQueue();

        Assert.Empty(queue.MarkReady());
    }

    // Identity, not just count: the request is handed to the primary verbatim
    // and is the only record of what the user asked for, so anything the queue
    // did to normalise it would be a silent edit of someone's command line.
    [Fact]
    public void HeldLaunches_ReplayOldestFirst_WithIdentityPreserved()
    {
        var queue = new LaunchDeferralQueue();
        var first = Req(@"C:\a", "wintty", "--flag");
        var second = Req(@"C:\b");
        var third = Req(@"C:\c", "", "x:y");

        Assert.True(queue.Defer(first));
        Assert.True(queue.Defer(second));
        Assert.True(queue.Defer(third));

        var drained = queue.MarkReady();
        Assert.Equal(3, drained.Count);
        Assert.Same(first, drained[0]);
        Assert.Same(second, drained[1]);
        Assert.Same(third, drained[2]);
    }

    [Fact]
    public void MarkReady_LatchesAndEmpties()
    {
        var queue = new LaunchDeferralQueue();
        queue.Defer(Req());

        Assert.False(queue.IsReady);
        Assert.Equal(1, queue.Count);

        Assert.Single(queue.MarkReady());
        Assert.True(queue.IsReady);
        Assert.Empty(queue.MarkReady());
    }

    // The whole reason the queue exists is that a launch the app could not act
    // on must not be lost. Readiness is one-way, so a request turned away
    // after it is the one case where a loss is unavoidable and has to be
    // visible to the caller rather than parked where nobody will look.
    [Fact]
    public void AfterReadiness_NothingIsHeld()
    {
        var queue = new LaunchDeferralQueue();
        queue.MarkReady();

        Assert.False(queue.Defer(Req(), out var evicted));
        Assert.Null(evicted);
        Assert.Equal(0, queue.Count);
    }

    // Cap, not refusal: the ninth launch is held and the first is the one
    // given up, because the newest is the launch the user is waiting on and
    // the oldest is the one most likely superseded by it.
    [Fact]
    public void PastCapacity_TheOldestIsEvicted_AndTheNewestIsHeld()
    {
        var queue = new LaunchDeferralQueue();
        var held = new List<LaunchRequest>();
        for (var i = 0; i < LaunchDeferralQueue.Capacity; i++)
        {
            var req = Req($@"C:\n{i}");
            held.Add(req);
            Assert.True(queue.Defer(req, out var evicted));
            Assert.Null(evicted);
        }

        var ninth = Req(@"C:\n-last");
        Assert.True(queue.Defer(ninth, out var dropped));

        Assert.Same(held[0], dropped);
        Assert.Equal(LaunchDeferralQueue.Capacity, queue.Count);

        var drained = queue.MarkReady();
        Assert.Equal(LaunchDeferralQueue.Capacity, drained.Count);
        Assert.Same(held[1], drained[0]);
        Assert.Same(ninth, drained[^1]);
    }

    // Two overflows in a row have to move the front each time. Asserting on
    // the working directory rather than on identity is what makes that
    // visible: the same reference evicted twice would still satisfy a
    // not-null check.
    [Fact]
    public void RepeatedOverflow_EvictsTheCurrentOldest_EachTime()
    {
        var queue = new LaunchDeferralQueue();
        for (var i = 0; i < LaunchDeferralQueue.Capacity; i++)
            queue.Defer(Req($@"C:\n{i}"));

        queue.Defer(Req(@"C:\first-overflow"), out var first);
        queue.Defer(Req(@"C:\second-overflow"), out var second);

        Assert.Equal(@"C:\n0", first!.WorkingDirectory);
        Assert.Equal(@"C:\n1", second!.WorkingDirectory);
    }

    [Fact]
    public void NullRequest_Throws()
    {
        var queue = new LaunchDeferralQueue();

        Assert.Throws<ArgumentNullException>(() => queue.Defer(null!));
    }
}
