using System;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The last-resort rebuild's executor, shared by both tab strips. MUXC's
/// item collections refuse writes while one of their own modifications is
/// still open -- and with virtualized hosts that state SPANS frames:
/// container generation completes across layout passes, so a single
/// dispatcher yield is not always enough. A refused rebuild is re-queued
/// instead; every attempt re-runs the whole rebuild, which re-reads
/// manager truth at run time (the rebuilds walk the projection fresh), so
/// no attempt carries stale pre-yield state. Only the foreign-frame
/// refusal (0x8000FFFF) is retried; genuine skew is not this class's to
/// swallow -- the last attempt's failure propagates.
/// </summary>
public static class ReconcileRetry
{
    private const int MaxAttempts = 8;
    private static readonly int NestedModification =
        unchecked((int)0x8000FFFF);

    /// <summary>Run <paramref name="attempt"/> now; on the foreign-frame
    /// refusal hand <paramref name="yield"/> the continuation -- the
    /// caller owns the dispatcher (Core has none) -- so the next attempt
    /// runs after a pump. Every attempt re-reads manager truth at run
    /// time; the budget bounds the yields, and exhaustion rethrows the
    /// refusal on the frame that gave up.
    /// </summary>
    public static void Rebuild(
        string what, Action attempt, Action landed, Action<string> trace,
        Action<Action> yield, int n = 1)
    {
        try
        {
            attempt();
        }
        catch (COMException ex) when (n < MaxAttempts && ex.HResult == NestedModification)
        {
            trace($"{what} deferred off a foreign frame (attempt {n})");
            yield(() => Rebuild(what, attempt, landed, trace, yield, n + 1));
            return;
        }
        landed();
        trace($"{what} landed on attempt {n}");
    }
}
