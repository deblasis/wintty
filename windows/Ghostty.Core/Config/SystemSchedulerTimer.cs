using System;
using System.Diagnostics;
using System.Threading;

namespace Ghostty.Core.Config;

/// <summary>
/// Real-time <see cref="ISchedulerTimer"/> backed by
/// <see cref="System.Threading.Timer"/>. The callback runs on a
/// threadpool thread; the caller is responsible for marshaling onto
/// the UI thread if needed.
/// </summary>
public sealed class SystemSchedulerTimer : ISchedulerTimer
{
    private readonly Timer _timer;

    public SystemSchedulerTimer()
    {
        _timer = new Timer(_ => Callback?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Action? Callback { get; set; }

    public void Schedule(TimeSpan delay)
        => _timer.Change(delay, Timeout.InfiniteTimeSpan);

    public void Cancel()
        => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose()
    {
        // Wait for any in-flight callback to finish before returning
        // so disposal happens-before the host disposes the config
        // editor. Without this, a just-fired timer callback could
        // run SetValue on a disposed editor after ConfigWriteScheduler
        // (and its consumers) have torn down.
        //
        // Bounded wait: callbacks are tiny (a debounced config write)
        // so 2s is already pathological. A timeout prevents a hung
        // callback from deadlocking app shutdown, since Dispose runs
        // on the UI thread.
        using var done = new ManualResetEvent(false);
        _timer.Dispose(done);
        if (!done.WaitOne(TimeSpan.FromSeconds(2)))
            Debug.WriteLine("[SystemSchedulerTimer] Dispose timed out waiting for callback");
    }
}
