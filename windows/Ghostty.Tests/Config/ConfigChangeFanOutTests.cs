using System;
using System.Collections.Generic;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The fan-out runs inside a DispatcherQueueHandler, where an escaping
/// exception fail-fasts the process instead of unwinding. These cover the
/// two properties that keeps safe: nothing propagates, and one faulting
/// subscriber does not starve the others.
/// </summary>
public class ConfigChangeFanOutTests
{
    private sealed record Service(string Name);

    [Fact]
    public void InvokeAll_WithArg_DeliversToEverySubscriber()
    {
        var seen = new List<string>();
        Action<Service> handlers = _ => seen.Add("first");
        handlers += _ => seen.Add("second");
        handlers += _ => seen.Add("third");

        ConfigChangeFanOut.InvokeAll(handlers, new Service("cfg"), _ => Assert.Fail("no fault expected"));

        Assert.Equal(new[] { "first", "second", "third" }, seen);
    }

    [Fact]
    public void InvokeAll_WithArg_PassesTheArgumentThrough()
    {
        Service? received = null;
        Action<Service> handlers = s => received = s;
        var arg = new Service("cfg");

        ConfigChangeFanOut.InvokeAll(handlers, arg, _ => Assert.Fail("no fault expected"));

        Assert.Same(arg, received);
    }

    /// <summary>
    /// The regression this type exists for. A plain multicast invoke stops at
    /// the first subscriber that throws; every window subscribes for its own
    /// chrome, so that would leave the later windows on a stale config.
    /// </summary>
    [Fact]
    public void InvokeAll_WithArg_LaterSubscribersStillRunAfterOneThrows()
    {
        var seen = new List<string>();
        Action<Service> handlers = _ => seen.Add("first");
        handlers += _ => throw new InvalidOperationException("chrome blew up");
        handlers += _ => seen.Add("third");

        var faults = new List<Exception>();
        ConfigChangeFanOut.InvokeAll(handlers, new Service("cfg"), faults.Add);

        Assert.Equal(new[] { "first", "third" }, seen);
        var fault = Assert.Single(faults);
        Assert.Equal("chrome blew up", fault.Message);
    }

    [Fact]
    public void InvokeAll_WithArg_ReportsEveryFaultSeparately()
    {
        Action<Service> handlers = _ => throw new InvalidOperationException("one");
        handlers += _ => throw new InvalidOperationException("two");

        var faults = new List<Exception>();
        ConfigChangeFanOut.InvokeAll(handlers, new Service("cfg"), faults.Add);

        Assert.Equal(new[] { "one", "two" }, faults.ConvertAll(e => e.Message));
    }

    [Fact]
    public void InvokeAll_WithArg_NullHandlersIsANoOp()
    {
        ConfigChangeFanOut.InvokeAll((Action<Service>?)null, new Service("cfg"),
            _ => Assert.Fail("no fault expected"));
    }

    /// <summary>
    /// A logger that throws must not resurrect the fail-fast the whole type
    /// exists to prevent.
    /// </summary>
    [Fact]
    public void InvokeAll_WithArg_SurvivesAFaultReporterThatThrows()
    {
        var reached = false;
        Action<Service> handlers = _ => throw new InvalidOperationException("subscriber");
        handlers += _ => reached = true;

        ConfigChangeFanOut.InvokeAll(handlers, new Service("cfg"),
            _ => throw new InvalidOperationException("logger is down too"));

        Assert.True(reached);
    }

    [Fact]
    public void InvokeAll_Parameterless_DeliversToEverySubscriber()
    {
        var seen = new List<string>();
        Action handlers = () => seen.Add("first");
        handlers += () => seen.Add("second");

        ConfigChangeFanOut.InvokeAll(handlers, _ => Assert.Fail("no fault expected"));

        Assert.Equal(new[] { "first", "second" }, seen);
    }

    [Fact]
    public void InvokeAll_Parameterless_LaterSubscribersStillRunAfterOneThrows()
    {
        var seen = new List<string>();
        Action handlers = () => throw new InvalidOperationException("profiles blew up");
        handlers += () => seen.Add("second");

        var faults = new List<Exception>();
        ConfigChangeFanOut.InvokeAll(handlers, faults.Add);

        Assert.Equal(new[] { "second" }, seen);
        Assert.Single(faults);
    }

    [Fact]
    public void InvokeAll_Parameterless_NullHandlersIsANoOp()
    {
        ConfigChangeFanOut.InvokeAll(null, _ => Assert.Fail("no fault expected"));
    }

    /// <summary>
    /// A subscriber that unsubscribes another mid-fan-out must not skip or
    /// double-invoke anyone: GetInvocationList snapshots, so the list this
    /// walks is the one that was current when the reload fired.
    /// </summary>
    [Fact]
    public void InvokeAll_WalksASnapshotOfTheSubscribers()
    {
        var seen = new List<string>();
        Action? handlers = null;
        Action second = () => seen.Add("second");
        handlers += () => { seen.Add("first"); handlers -= second; };
        handlers += second;

        ConfigChangeFanOut.InvokeAll(handlers, _ => Assert.Fail("no fault expected"));

        Assert.Equal(new[] { "first", "second" }, seen);
    }
}
