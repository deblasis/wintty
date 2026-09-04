using System;
using System.Threading;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Marks background tabs idle when a session has received no data and no
/// user interaction for a minute, so the strip can dim them and show the
/// sleeping state (the moon). It wakes on either: arriving data or a
/// touch.
///
/// The signal is pull-based: every surface keeps its own
/// <c>TerminalControl.LastActivityTick</c> stamp (keystrokes, pointer
/// presses, and each callback libghostty fires for arriving data that
/// changes observable state -- title, cwd, progress, bell, scrollbar),
/// the pane host aggregates its leaves into
/// <see cref="IPaneHost.LastActivityTick"/>, and this tracker's periodic
/// sweep reads the aggregates. No per-tab event subscriptions means
/// nothing to unwire when a tab closes: a closed tab simply stops being
/// in <see cref="TabManager.Tabs"/>.
///
/// <see cref="TabModel.IsIdle"/> has exactly one writer in the product --
/// <see cref="Sweep"/> (plus the eager clear when a tab is activated, in
/// <see cref="Start"/>'s activation handler). The sweep rules:
/// the active tab is never idle; a tab with an unacknowledged bell is
/// never idle (the pane just received something worth attention, and the
/// bell glyph owns the badge slot); a tab whose newest stamp is older
/// than the threshold is idle. Activation also stamps BOTH tabs involved
/// -- visiting a tab is interacting with it, so a tab you just read for
/// ten seconds does not dim seconds after you leave it.
/// </summary>
internal sealed class TabIdleTracker : IDisposable
{
    /// <summary>
    /// How long a background session goes without data or interaction
    /// before it reads as idle. One minute: long enough that an active
    /// working session never flickers the state on, short enough that a
    /// tab parked since morning is visibly asleep.
    /// </summary>
    internal static readonly TimeSpan DefaultIdleAfter = TimeSpan.FromMinutes(1);

    // The sweep period bounds how stale IsIdle can be: a state change
    // that arrives through a stamp (rather than an activation) is
    // reflected on the next sweep, so at most this far behind.
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);

    private readonly TabManager _manager;
    private readonly double _idleAfterMs;
    private readonly Func<long> _clock;
    private readonly Action<Action> _marshal;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _timer;
    private TabModel? _lastActive;
    private bool _disposed;

    /// <param name="manager">The window's tab manager. One tracker per
    /// manager; the tracker shares the manager's lifetime.</param>
    /// <param name="marshal">How the sweep reaches the thread that owns
    /// the models (the UI thread). The timer fires on a threadpool
    /// thread and the sweep writes INPC properties.</param>
    /// <param name="idleAfter">Override of <see cref="DefaultIdleAfter"/>,
    /// for tests.</param>
    /// <param name="clock">Activity-clock source in
    /// <see cref="Environment.TickCount64"/> milliseconds, for tests.
    /// Stamps and reads must share one domain; only differences are
    /// used, so any monotonic base works.</param>
    /// <param name="time">Timer provider, for tests.</param>
    public TabIdleTracker(
        TabManager manager,
        Action<Action> marshal,
        TimeSpan? idleAfter = null,
        Func<long>? clock = null,
        TimeProvider? time = null)
    {
        _manager = manager;
        _idleAfterMs = (idleAfter ?? DefaultIdleAfter).TotalMilliseconds;
        _clock = clock ?? DefaultClock;
        _marshal = marshal;
        _time = time ?? TimeProvider.System;
    }

    private static long DefaultClock() => Environment.TickCount64;

    /// <summary>
    /// Arm the shared sweep and start tracking activations. Idempotent.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _lastActive = _manager.ActiveTab;
            _manager.ActiveTabChanged += OnActiveTabChanged;
            _timer = _time.CreateTimer(
                _ => _marshal(Sweep),
                null,
                SweepPeriod,
                SweepPeriod);
        }
        // First pass now, so a window restored onto long-untouched tabs
        // shows them asleep as soon as it draws rather than after the
        // first period elapses.
        _marshal(Sweep);
    }

    private void OnActiveTabChanged(object? sender, TabModel tab)
    {
        // Switching to a tab is the user touching it, and leaving one is
        // the moment its idle clock should start: stamp both, then clear
        // the eager way so the moon lifts on activation, not on the next
        // sweep.
        if (_lastActive is { } previous) previous.LastActivityTick = _clock();
        tab.LastActivityTick = _clock();
        tab.IsIdle = false;
        _lastActive = tab;
    }

    /// <summary>
    /// Recompute <see cref="TabModel.IsIdle"/> for every tab from the
    /// current stamps. Public entry for the timer via the marshal;
    /// called directly by tests.
    /// </summary>
    internal void Sweep()
    {
        if (_disposed) return;
        var now = _clock();
        var active = _manager.ActiveTab;
        foreach (var tab in _manager.Tabs)
        {
            // 0 means "no stamp yet" (a leaf that has not loaded, or a
            // restored tab whose replay has not produced its first
            // signal): treat as fresh rather than ancient, so nothing
            // is born asleep.
            var last = Math.Max(tab.LastActivityTick, tab.PaneHost.LastActivityTick);
            tab.IsIdle = last != 0
                && !ReferenceEquals(tab, active)
                && !tab.BellRinging
                && now - last >= _idleAfterMs;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _manager.ActiveTabChanged -= OnActiveTabChanged;
        }
    }
}
