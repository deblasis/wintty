using System;

namespace Ghostty.Core.Config;

/// <summary>
/// Minimal timer abstraction so <see cref="ConfigWriteScheduler"/>
/// can be unit-tested without real clocks. The production
/// implementation is <see cref="SystemSchedulerTimer"/>; tests swap
/// in a fake that fires <see cref="Callback"/> on demand.
/// </summary>
public interface ISchedulerTimer : IDisposable
{
    Action? Callback { get; set; }

    /// <summary>
    /// Arm the timer to fire <see cref="Callback"/> once after
    /// <paramref name="delay"/>. Re-arming replaces any pending fire
    /// (last schedule wins, matching the coalescing semantics).
    /// </summary>
    void Schedule(TimeSpan delay);

    /// <summary>Cancel any pending fire.</summary>
    void Cancel();
}
