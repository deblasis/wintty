using System;
using System.Collections.Generic;
using System.ComponentModel;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Taskbar;

/// <summary>
/// Cycles the Windows taskbar progress indicator across tabs that
/// are reporting OSC 9;4 progress. Pure-logic state machine; the
/// WinUI-side facade writes to <c>ITaskbarList3</c>.
///
/// State model (implicit in _active list length):
///   Clear:    _active.Count == 0. Sink is told None.
///   Single:   _active.Count == 1. Live updates to that tab pass
///             through immediately.
///   Cycling:  _active.Count >= 2. A <see cref="Tick"/> call
///             advances <see cref="_currentIndex"/> and emits the
///             new current tab's state. Live updates to the
///             currently displayed tab pass through; updates to
///             other tabs update <see cref="TabModel.Progress"/>
///             but do not touch the sink.
///
/// Time is injected via the <c>nowProvider</c> so tests can control
/// it. Real call site uses <see cref="DateTime.UtcNow"/> and a
/// <c>DispatcherQueueTimer</c> to drive <see cref="Tick"/> on a
/// 2-second cadence.
/// </summary>
internal sealed class TaskbarProgressCoordinator : IDisposable
{
    private const double SlotDurationMs = 2000.0;

    private readonly TabManager _manager;
    private readonly ITaskbarProgressSink _sink;
    private readonly Func<DateTime> _nowProvider;
    private readonly List<TabModel> _active = new();
    private readonly EventHandler<TabModel> _onTabAdded;
    private readonly EventHandler<TabModel> _onTabRemoved;
    private int _currentIndex = -1;
    private DateTime _slotStart;
    private bool _paused;
    private bool _disposed;

    public TaskbarProgressCoordinator(TabManager manager, ITaskbarProgressSink sink, Func<DateTime> nowProvider)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(nowProvider);

        _manager = manager;
        _sink = sink;
        _nowProvider = nowProvider;
        _slotStart = nowProvider();

        _onTabAdded = (_, t) => Subscribe(t);
        _onTabRemoved = (_, t) => { Unsubscribe(t); ForceRemove(t); };

        foreach (var t in _manager.Tabs) Subscribe(t);
        _manager.TabAdded += _onTabAdded;
        _manager.TabRemoved += _onTabRemoved;
    }

    public void Pause() => _paused = true;

    public void Resume()
    {
        _paused = false;
        _slotStart = _nowProvider();
    }

    /// <summary>
    /// Driven by a DispatcherQueueTimer at ~2 seconds in production;
    /// tests call manually after advancing their clock.
    /// </summary>
    public void Tick()
    {
        if (_paused) return;
        if (_active.Count < 2) return;

        var elapsed = (_nowProvider() - _slotStart).TotalMilliseconds;
        if (elapsed < SlotDurationMs) return;

        _currentIndex = (_currentIndex + 1) % _active.Count;
        _slotStart = _nowProvider();
        _sink.Write(_active[_currentIndex].Progress);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.TabAdded -= _onTabAdded;
        _manager.TabRemoved -= _onTabRemoved;
        foreach (var t in _manager.Tabs) Unsubscribe(t);
        _active.Clear();
    }

    private void Subscribe(TabModel tab) => tab.PropertyChanged += OnTabPropertyChanged;

    private void Unsubscribe(TabModel tab) => tab.PropertyChanged -= OnTabPropertyChanged;

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabModel.Progress)) return;
        if (sender is TabModel t) HandleProgressChange(t);
    }

    /// <summary>
    /// Called when a tab is removed from the manager. If the tab was
    /// currently in the active list we force a "no progress" transition
    /// so the state machine drops it cleanly — we can't trust
    /// <see cref="TabModel.Progress"/> here because the closing tab may
    /// still be mid-report.
    /// </summary>
    private void ForceRemove(TabModel tab)
    {
        if (!_active.Contains(tab)) return;
        var wasCurrent = _active[_currentIndex] == tab;
        _active.Remove(tab);

        if (_active.Count == 0)
        {
            _currentIndex = -1;
            _sink.Write(TabProgressState.None);
            return;
        }

        if (_currentIndex >= _active.Count) _currentIndex = 0;

        if (_active.Count == 1)
        {
            _currentIndex = 0;
            _sink.Write(_active[0].Progress);
            return;
        }

        if (wasCurrent)
            _sink.Write(_active[_currentIndex].Progress);
    }

    private void HandleProgressChange(TabModel tab)
    {
        var inList = _active.Contains(tab);
        var hasProgress = tab.Progress.State != TabProgressState.Kind.None;

        if (hasProgress && !inList)
        {
            _active.Add(tab);
            if (_active.Count == 1)
            {
                _currentIndex = 0;
                _slotStart = _nowProvider();
                _sink.Write(tab.Progress); // Clear -> Single
            }
            else if (_active.Count == 2)
            {
                // Single -> Cycling. Emit the currently displayed
                // tab's state to re-sync the sink (idempotent in the
                // common case where Single was already showing it).
                _sink.Write(_active[_currentIndex].Progress);
            }
            // 3+ joining just sits in the list until its slot comes.
            return;
        }

        if (!hasProgress && inList)
        {
            ForceRemove(tab);
            return;
        }

        if (hasProgress && inList)
        {
            // Live update. Pass through only if this IS the current
            // displayed tab in Single or the current-slot tab in
            // Cycling.
            if (_active.Count == 1 || _active[_currentIndex] == tab)
                _sink.Write(tab.Progress);
        }
    }
}
