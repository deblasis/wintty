using Ghostty.Core.DefaultTerm;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The in-process placement queue: bounded, parked from the COM thread,
/// drained on the UI thread when a surface host exists, with the
/// most-recent-window rule and per-item budgets. All against fakes: no
/// test opens a window, and no test touches the single-instance pipe.
/// The forwarding probe is a canary the design says can never fire: a
/// secondary receiver hosts in its own window and never forwards.
/// </summary>
public sealed class PlacementQueueTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PreWindowHandoff_QueuesAndOpensWithFirstWindow()
    {
        var now = T0;
        var queue = new DefaultTermPlacementQueue(4, () => now);
        queue.DrainRequested = () => queue.Drain();
        var host = new FakeHost { Any = "W1" };

        var parked = HandoffPlacementResult.Timeout;
        var comThread = new Thread(() =>
            parked = queue.ParkAndWait(MakeRequest(), TimeSpan.FromSeconds(5)));
        comThread.Start();
        WaitUntil(() => queue.Pending == 1);

        // No host yet: the item stays queued, the caller still parked.
        Assert.Equal(0, host.Adoptions.Count);

        queue.AttachHost(host);
        comThread.Join(TimeSpan.FromSeconds(10));

        Assert.Equal(HandoffPlacementResult.Adopted, parked);
        var adoption = Assert.Single(host.Adoptions);
        // No last-activated window exists, so the rule falls to any
        // window: the session opens with the first window the host has.
        Assert.Equal("W1", adoption.Window);
    }

    [Fact]
    public void BudgetExpiry_DegradesThatLaunchOnly()
    {
        var now = T0;
        var queue = new DefaultTermPlacementQueue(4, () => now);
        queue.DrainRequested = () => queue.Drain();
        var host = new FakeHost { Any = "W1" };

        var expiredParked = HandoffPlacementResult.Adopted;
        var survivorParked = HandoffPlacementResult.Timeout;
        var expiredComplete = HandoffPlacementResult.Adopted;
        var survivorComplete = HandoffPlacementResult.Timeout;

        var t1 = new Thread(() => expiredParked = queue.ParkAndWait(
            MakeRequest(r => expiredComplete = r), TimeSpan.FromSeconds(1)));
        t1.Start();
        WaitUntil(() => queue.Pending == 1);

        var t2 = new Thread(() => survivorParked = queue.ParkAndWait(
            MakeRequest(r => survivorComplete = r), TimeSpan.FromSeconds(30)));
        t2.Start();
        WaitUntil(() => queue.Pending == 2);

        // The first item's budget is spent; the second's is not.
        now = T0.AddSeconds(2);

        queue.AttachHost(host);
        t1.Join(TimeSpan.FromSeconds(10));
        t2.Join(TimeSpan.FromSeconds(10));

        // The expired launch degrades on its own: its caller gets the
        // failure completion and never reaches a window.
        Assert.Equal(HandoffPlacementResult.Timeout, expiredParked);
        Assert.Equal(HandoffPlacementResult.Timeout, expiredComplete);

        // The later item is unaffected: it is placed.
        Assert.Equal(HandoffPlacementResult.Adopted, survivorParked);
        Assert.Equal(HandoffPlacementResult.Adopted, survivorComplete);
        Assert.Single(host.Adoptions);
    }

    [Fact]
    public void MostRecentWindowRule_FallsBackInOrder()
    {
        var queue = new DefaultTermPlacementQueue(4, () => T0);
        queue.DrainRequested = () => queue.Drain();
        var host = new FakeHost();

        // Last-activated wins when one exists.
        host.Last = "W2";
        Place(queue, host);
        Assert.Equal("W2", Assert.Single(host.Adoptions).Window);

        // Then any window.
        host.Adoptions.Clear();
        host.Last = null;
        host.Any = "W3";
        Place(queue, host);
        Assert.Equal("W3", Assert.Single(host.Adoptions).Window);

        // Then a new window.
        host.Adoptions.Clear();
        host.Any = null;
        Place(queue, host);
        Assert.Null(Assert.Single(host.Adoptions).Window);
    }

    [Fact]
    public void SecondaryReceiver_HostsInOwnWindow_NoForwarding()
    {
        var queue = new DefaultTermPlacementQueue(4, () => T0);
        queue.DrainRequested = () => queue.Drain();
        var host = new FakeHost { Last = "own-window" };

        var forwarded = new List<HandoffPlacementRequest>();
        queue.ForwardingProbe = forwarded.Add;

        var parked = Place(queue, host);

        Assert.Equal(HandoffPlacementResult.Adopted, parked);
        // The session lands in this process's own window...
        Assert.Equal("own-window", Assert.Single(host.Adoptions).Window);
        // ...and the single-instance forwarding path is never touched:
        // a secondary receiver hosts, it does not forward.
        Assert.Empty(forwarded);
    }

    [Fact]
    public void BoundedQueue_RefusesPastCapacity()
    {
        var queue = new DefaultTermPlacementQueue(1, () => T0);
        queue.DrainRequested = () => queue.Drain();

        var first = new Thread(() => queue.ParkAndWait(
            MakeRequest(), TimeSpan.FromSeconds(30)));
        first.Start();
        WaitUntil(() => queue.Pending == 1);

        // The capacity is one: the second park is refused immediately,
        // its launch degrades, and the queued one is untouched.
        Assert.Equal(
            HandoffPlacementResult.Timeout,
            queue.ParkAndWait(MakeRequest(), TimeSpan.FromSeconds(30)));
        Assert.Equal(1, queue.Pending);
    }

    private static HandoffPlacementResult Place(
        DefaultTermPlacementQueue queue,
        IDefaultTermSurfaceHost host)
    {
        queue.AttachHost(host);
        var result = queue.ParkAndWait(MakeRequest(), TimeSpan.FromSeconds(5));
        return result;
    }

    private static HandoffPlacementRequest MakeRequest(
        Action<HandoffPlacementResult>? observed = null) =>
        new(
            new HandoffArgs(1, 2, 3, 4, 5),
            r =>
            {
                observed?.Invoke(r);
                return r == HandoffPlacementResult.Adopted
                    ? 0
                    : unchecked((int)0x80004005);
            });

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("condition never became true");
    }

    private sealed class FakeHost : IDefaultTermSurfaceHost
    {
        public object? Last { get; set; }

        public object? Any { get; set; }

        public List<(object? Window, HandoffPlacementRequest Request)> Adoptions { get; } = new();

        public object? LastActivatedWindow() => Last;

        public object? AnyWindow() => Any;

        public bool Adopt(object? window, HandoffPlacementRequest request)
        {
            Adoptions.Add((window, request));
            return true;
        }
    }
}
