using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Polling implementation of <see cref="IActiveProcessTracker"/>. One
/// <see cref="Timer"/>, self-scheduled so ticks never overlap. Each tick:
///  1. Snapshots the registered root PIDs.
///  2. Resolves every root against ONE process snapshot via
///     <see cref="ProcessTreeWalker.FindInnermostDescendants"/>.
///  3. Runs each result through <see cref="ActiveProcessDebouncer"/>.
///  4. Raises <see cref="Changed"/> for every emission.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
public sealed class WindowsActiveProcessTracker : IActiveProcessTracker
{
    private const int TickIntervalMs = 500;
    private const int DebounceWindowMs = 250;
    // The longest the adaptive cadence may stretch between walks.
    private const int MaxDueMs = 30_000;

    private readonly Timer _timer;
    private readonly ConcurrentDictionary<int, byte> _roots = new();
    private readonly ActiveProcessDebouncer _debouncer = new(DebounceWindowMs);
    private int _disposed;

    public event EventHandler<ActiveProcessChangedEventArgs>? Changed;

    public WindowsActiveProcessTracker()
    {
        // period: never. A fixed period re-enters OnTick while a slow tick
        // (one full-machine process snapshot) is still walking, and the
        // concurrent ticks pile up without bound - measured at more than a
        // full core, from launch, on a process-heavy machine. The tick
        // re-arms itself when its work is done, so the real cadence is one
        // walk every (interval + work) and no walk ever overlaps another.
        _timer = new Timer(OnTick, state: null,
            dueTime: TickIntervalMs, period: Timeout.Infinite);
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
        var walkStart = Environment.TickCount64;
        try
        {
            var now = Environment.TickCount64;

            // One snapshot for every root: the walk's cost is the whole
            // machine's process count, so per-root snapshots multiply a
            // machine-wide cost by the tab count.
            IReadOnlyDictionary<uint, DescendantInfo?> infos;
            try
            {
                infos = ProcessTreeWalker.FindInnermostDescendants(
                    _roots.Keys.Select(p => (uint)p));
            }
            catch (Exception ex)
            {
                // Snapshot failures and access-denied during a teardown race are
                // common; suppress and treat as "no foreground" but record so a
                // regressed tracker is diagnosable from the debug log.
                System.Diagnostics.Debug.WriteLine(
                    $"WindowsActiveProcessTracker: snapshot failed: {ex.GetType().Name}: {ex.Message}");
                infos = new Dictionary<uint, DescendantInfo?>();
            }

            foreach (var (rootPid, _) in _roots)
            {
                DescendantInfo? info = infos.GetValueOrDefault((uint)rootPid);
                var exe = info?.ExeBasename;
                var cmdline = info?.CommandLine;
                var emission = _debouncer.Observe(rootPid, exe, commandLine: cmdline, nowMs: now);
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
        finally
        {
            // The self-schedule: the next walk is due AFTER this one
            // finished, never while it runs, and never more often than the
            // walk itself can be paid for - on a process-heavy box one
            // snapshot costs a large fraction of the interval, and polling
            // at a fixed 2Hz spends most of a core on icon tracking. The
            // 3x floor caps the duty cycle at a quarter (due time counts
            // from the tick's end); quiet machines never notice it. The
            // 30s ceiling bounds how long a transiently thrashing box can
            // stretch the tracker's dead time. ObjectDisposed races the
            // Dispose wait below; a lost re-arm is the shutdown case anyway.
            var walkMs = Environment.TickCount64 - walkStart;
            var dueMs = Math.Min(MaxDueMs, Math.Max(TickIntervalMs, 3 * (int)walkMs));
            try { _timer.Change(dueMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
    }
}
