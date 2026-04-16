using System;

namespace Ghostty.Core.Config;

/// <summary>
/// Queues live config mutations and flushes them to
/// <see cref="IConfigFileEditor"/> after a short debounce window.
/// Same-key writes coalesce (last value wins) so slider drags do
/// not churn the config file. Consumers must still update the live
/// runtime state themselves -- the scheduler only handles
/// persistence, not UI effects.
/// </summary>
public interface IConfigWriteScheduler : IDisposable
{
    /// <summary>
    /// Queue a write. If <paramref name="key"/> already has a pending
    /// write, the previous value is replaced. Rearms the debounce
    /// timer so a burst of calls flushes once at the tail end.
    /// </summary>
    void Schedule(string key, string value);

    /// <summary>
    /// Synchronously flush all pending writes. Safe to call any
    /// time; no-op if nothing is queued. Used on app shutdown and
    /// at explicit checkpoints (e.g. settings window closing).
    /// </summary>
    void Flush();
}
