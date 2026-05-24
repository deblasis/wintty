using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Polling implementation of <see cref="IActiveProcessTracker"/>. One
/// <see cref="Timer"/>, two ticks per second. Each tick:
///  1. Snapshots the registered root PIDs.
///  2. Walks the tree once per root via <see cref="ProcessTreeWalker"/>.
///  3. Runs each result through <see cref="ActiveProcessDebouncer"/>.
///  4. Raises <see cref="Changed"/> for every emission.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
public sealed class WindowsActiveProcessTracker : IActiveProcessTracker
{
    private const int TickIntervalMs = 500;
    private const int DebounceWindowMs = 250;

    private readonly Timer _timer;
    private readonly ConcurrentDictionary<int, byte> _roots = new();
    private readonly ActiveProcessDebouncer _debouncer = new(DebounceWindowMs);
    private int _disposed;

    public event EventHandler<ActiveProcessChangedEventArgs>? Changed;

    public WindowsActiveProcessTracker()
    {
        _timer = new Timer(OnTick, state: null, dueTime: TickIntervalMs, period: TickIntervalMs);
    }

    public void Register(int rootPid) => _roots.TryAdd(rootPid, 0);

    public void Unregister(int rootPid)
    {
        _roots.TryRemove(rootPid, out _);
        _debouncer.Forget(rootPid);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        using var done = new System.Threading.ManualResetEvent(false);
        _timer.Dispose(done);
        // Block briefly so any in-flight tick finishes before we return.
        // OnActiveProcessChanged subscribers will see _disposed != 0 on
        // re-entry and short-circuit.
        done.WaitOne();
    }

    private void OnTick(object? state)
    {
        if (_disposed != 0) return;
        var now = Environment.TickCount64;

        foreach (var (rootPid, _) in _roots)
        {
            DescendantInfo? info;
            try
            {
                info = ProcessTreeWalker.FindInnermostDescendant((uint)rootPid);
            }
            catch (Exception ex)
            {
                // Snapshot failures and access-denied during a teardown race are
                // common; suppress and treat as "no foreground" but record so a
                // regressed tracker is diagnosable from the debug log.
                System.Diagnostics.Debug.WriteLine(
                    $"WindowsActiveProcessTracker: snapshot failed for pid={rootPid}: {ex.GetType().Name}: {ex.Message}");
                info = null;
            }

            var exe = info?.ExeBasename;
            // The walker can now retrieve the command line of the leaf;
            // wiring it through the debouncer is intentionally deferred
            // to the next commit so this change stays purely about the
            // walker's API surface.
            var emission = _debouncer.Observe(rootPid, exe, commandLine: null, nowMs: now);
            if (emission is not null)
            {
                try
                {
                    Changed?.Invoke(this, new ActiveProcessChangedEventArgs(
                        emission.RootPid, emission.ExeBasename, emission.CommandLine));
                }
                catch (Exception ex)
                {
                    // Subscribers must not crash the tracker. Log the failing handler
                    // so it doesn't decay silently.
                    System.Diagnostics.Debug.WriteLine(
                        $"WindowsActiveProcessTracker: Changed subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
