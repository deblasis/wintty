using System;
using System.Collections.Generic;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Config;

/// <summary>
/// Default <see cref="IConfigWriteScheduler"/> implementation.
/// Thread-safe: <see cref="Schedule"/> and <see cref="Flush"/> may
/// be called from any thread, but the flush callback runs on the
/// timer's thread (threadpool for the production timer). The
/// post-flush <c>onFlushed</c> callback is the hook the Windows
/// host uses to invoke <see cref="IConfigService.Reload"/> on the
/// dispatcher queue.
/// </summary>
public sealed partial class ConfigWriteScheduler : IConfigWriteScheduler
{
    /// <summary>
    /// One queued write, with the outcome callback the caller handed in (null
    /// for the callers that do not care, which is most of them).
    /// </summary>
    private readonly record struct PendingWrite(
        string Value,
        Action<ConfigWriteOutcome>? OnWritten);

    private readonly IConfigFileEditor _editor;
    private readonly ISchedulerTimer _timer;
    private readonly TimeSpan _debounce;
    private readonly Action _onFlushed;
    private readonly ILogger<ConfigWriteScheduler> _logger;
    // OrdinalIgnoreCase matches ghostty's own config parser, so
    // Schedule("vertical-tabs") and Schedule("Vertical-Tabs") coalesce
    // the same way the downstream reader would treat them.
    private readonly Dictionary<string, PendingWrite> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public ConfigWriteScheduler(
        IConfigFileEditor editor,
        ISchedulerTimer timer,
        TimeSpan debounce,
        Action onFlushed,
        ILogger<ConfigWriteScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(onFlushed);
        ArgumentNullException.ThrowIfNull(logger);

        _editor = editor;
        _timer = timer;
        _debounce = debounce;
        _onFlushed = onFlushed;
        _logger = logger;
        _timer.Callback = FlushFromTimer;
    }

    public void Schedule(string key, string value, Action<ConfigWriteOutcome>? onWritten = null)
    {
        Action<ConfigWriteOutcome>? replaced = null;
        lock (_lock)
        {
            if (_disposed)
            {
                // Reported rather than dropped: the caller is waiting on this
                // answer to decide whether its guard moved, and a callback
                // that never runs leaves a guard describing a write that is
                // never going to happen. Nothing is queued, so nothing is
                // ever written -- which is what Superseded means.
                replaced = onWritten;
            }
            else
            {
                // The value being replaced will never be written, so it is
                // answered here rather than left waiting for a flush that
                // will not mention it. Invoked outside the lock.
                if (_pending.TryGetValue(key, out var previous))
                    replaced = previous.OnWritten;

                _pending[key] = new PendingWrite(value, onWritten);  // last write wins
                _timer.Schedule(_debounce);  // rearm trailing-edge timer
            }
        }

        Report(replaced, ConfigWriteOutcome.Superseded);
    }

    public void Flush() => DrainAndWrite(signal: true);

    private void FlushFromTimer() => DrainAndWrite(signal: true);

    private void DrainAndWrite(bool signal)
    {
        List<KeyValuePair<string, PendingWrite>>? snapshot;
        lock (_lock)
        {
            _timer.Cancel();
            if (_pending.Count == 0) return;
            snapshot = new List<KeyValuePair<string, PendingWrite>>(_pending);
            _pending.Clear();
        }
        var outcomes = WriteBatch(snapshot);
        if (signal) _onFlushed();
        // After the reload signal, so a caller told the value landed is not
        // told it before the app has been told to re-read the file.
        foreach (var (write, outcome, key) in outcomes)
            Report(write.OnWritten, outcome, key);
    }

    /// <summary>
    /// Write every entry, reporting each one's outcome instead of only
    /// logging the failures.
    /// </summary>
    /// <returns>
    /// The reports to make once the reload signal has gone, in batch order:
    /// the queued write, its outcome, and the key it was for.
    /// </returns>
    /// <remarks>
    /// One bad SetValue (disk full, file locked) must not drop
    /// the rest of the batch or skip the reload signal. Trace
    /// and continue so the user sees at least partial persistence.
    /// </remarks>
    private List<(PendingWrite Write, ConfigWriteOutcome Outcome, string Key)> WriteBatch(
        List<KeyValuePair<string, PendingWrite>> batch)
    {
        var outcomes = new List<(PendingWrite, ConfigWriteOutcome, string)>(batch.Count);
        foreach (var kv in batch)
        {
            var outcome = ConfigWriteOutcome.Written;
            try { _editor.SetValue(kv.Key, kv.Value.Value); }
            catch (Exception ex)
            {
                LogWriteFailed(ex, kv.Key);
                outcome = ConfigWriteOutcome.Failed;
            }
            outcomes.Add((kv.Value, outcome, kv.Key));
        }
        return outcomes;
    }

    /// <summary>
    /// Hand one caller its outcome. Never throws: this runs inside the flush,
    /// where a page's exception would take out the rest of the batch's
    /// reporting and, on the dispose path, the shutdown drain.
    /// </summary>
    private void Report(
        Action<ConfigWriteOutcome>? callback,
        ConfigWriteOutcome outcome,
        string? key = null)
    {
        if (callback is null) return;
        try
        {
            callback(outcome);
        }
        catch (Exception ex)
        {
            LogOutcomeCallbackFailed(ex, key ?? "(unspecified)");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Drain to disk but do NOT fire onFlushed: on app shutdown the
        // callback marshals a Reload() onto the UI dispatcher which
        // runs AFTER we return. By then the bootstrap host is about
        // to call AppFree on the ghostty app, so a reload through a
        // dangling handle is a use-after-free. Persistence is the only
        // thing that matters at shutdown; the reload is moot.
        DrainAndWrite(signal: false);
        _timer.Dispose();
    }

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.WriteSchedulerErr,
                   Level = LogLevel.Warning,
                   Message = "[ConfigWriteScheduler] SetValue('{Key}') failed")]
    private partial void LogWriteFailed(System.Exception ex, string key);

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.WriteSchedulerOutcomeErr,
                   Level = LogLevel.Warning,
                   Message = "[ConfigWriteScheduler] the onWritten callback for '{Key}' threw")]
    private partial void LogOutcomeCallbackFailed(System.Exception ex, string key);
}
