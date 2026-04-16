using System;
using System.Collections.Generic;
using System.Diagnostics;

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
public sealed class ConfigWriteScheduler : IConfigWriteScheduler
{
    private readonly IConfigFileEditor _editor;
    private readonly ISchedulerTimer _timer;
    private readonly TimeSpan _debounce;
    private readonly Action _onFlushed;
    // OrdinalIgnoreCase matches ghostty's own config parser, so
    // Schedule("vertical-tabs") and Schedule("Vertical-Tabs") coalesce
    // the same way the downstream reader would treat them.
    private readonly Dictionary<string, string> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public ConfigWriteScheduler(
        IConfigFileEditor editor,
        ISchedulerTimer timer,
        TimeSpan debounce,
        Action onFlushed)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(onFlushed);

        _editor = editor;
        _timer = timer;
        _debounce = debounce;
        _onFlushed = onFlushed;
        _timer.Callback = FlushFromTimer;
    }

    public void Schedule(string key, string value)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pending[key] = value;       // last write wins
            _timer.Schedule(_debounce);  // rearm trailing-edge timer
        }
    }

    public void Flush() => DrainAndWrite(signal: true);

    private void FlushFromTimer() => DrainAndWrite(signal: true);

    private void DrainAndWrite(bool signal)
    {
        List<KeyValuePair<string, string>>? snapshot;
        lock (_lock)
        {
            _timer.Cancel();
            if (_pending.Count == 0) return;
            snapshot = new List<KeyValuePair<string, string>>(_pending);
            _pending.Clear();
        }
        WriteBatch(snapshot);
        if (signal) _onFlushed();
    }

    private void WriteBatch(List<KeyValuePair<string, string>> batch)
    {
        // One bad SetValue (disk full, file locked) must not drop
        // the rest of the batch or skip the reload signal. Trace
        // and continue so the user sees at least partial persistence.
        foreach (var kv in batch)
        {
            try { _editor.SetValue(kv.Key, kv.Value); }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ConfigWriteScheduler] SetValue('{kv.Key}') failed: {ex.Message}");
            }
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
}
