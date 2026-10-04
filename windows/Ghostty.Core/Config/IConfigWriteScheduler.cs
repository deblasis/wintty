using System;

namespace Ghostty.Core.Config;

/// <summary>
/// What became of one <see cref="IConfigWriteScheduler.Schedule"/> call's
/// value, reported to its <c>onWritten</c> callback exactly once.
/// </summary>
public enum ConfigWriteOutcome
{
    /// <summary>
    /// The value reached the config file. The only outcome a caller may treat
    /// as "the user's edit is persisted": it is what lets a page move the
    /// guard that suppresses a repeat write of the same value.
    /// </summary>
    Written,

    /// <summary>
    /// The write threw (disk full, file locked, permission denied). The file
    /// still holds whatever it held before, and the failure is logged -- see
    /// the scheduler's write-error event. A caller that leaves its guard alone
    /// is then able to retry, which is the whole point of asking.
    /// </summary>
    Failed,

    /// <summary>
    /// The value will never be written by this scheduler: a later
    /// <c>Schedule</c> for the same key replaced it (same-key writes coalesce,
    /// last value wins), or the scheduler was already disposed. Not a failure
    /// -- but not a landing either, so it must not move a guard either. The
    /// replacement value reports its own outcome.
    /// </summary>
    Superseded,
}

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
    /// <param name="onWritten">
    /// Optional per-key outcome, invoked off the UI thread once this value's
    /// fate is known -- after the write for <see cref="ConfigWriteOutcome.Written"/>
    /// or <see cref="ConfigWriteOutcome.Failed"/>, and at replace time for
    /// <see cref="ConfigWriteOutcome.Superseded"/>. Every call that passes a
    /// callback gets exactly one invocation, so a caller can pair the two
    /// without a timer of its own: a page that suppresses a repeat write of
    /// the value in the file advances its guard from
    /// <see cref="ConfigWriteOutcome.Written"/> alone, and a value that never
    /// landed stays writeable. Exceptions from the callback are caught and
    /// logged, never propagated into the flush.
    /// </param>
    void Schedule(string key, string value, Action<ConfigWriteOutcome>? onWritten = null);

    /// <summary>
    /// Synchronously flush all pending writes. Safe to call any
    /// time; no-op if nothing is queued. Used on app shutdown and
    /// at explicit checkpoints (e.g. settings window closing).
    /// </summary>
    void Flush();
}
