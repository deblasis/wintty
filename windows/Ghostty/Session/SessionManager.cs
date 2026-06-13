using System;
using System.Collections.Generic;
using Ghostty.Core.Session;
using Ghostty.Services;
using Microsoft.UI.Dispatching;

namespace Ghostty.Session;

/// <summary>
/// Owns session persistence: a debounced "capture all live windows and
/// write" triggered by layout/tab/window changes (CleanShutdown=false),
/// plus a final clean write on app shutdown (CleanShutdown=true). Decides
/// at startup whether to restore. UI-thread affine.
/// </summary>
internal sealed class SessionManager
{
    // Coalesce bursts of layout/tab/move signals into a single write. Long
    // enough that a multi-window quit (windows close within this window of
    // one another) does not refire before FinalizeCleanShutdown runs.
    private const int DebounceMs = 750;

    private readonly SessionStore _store;
    private readonly ConfigService _config;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IEnumerable<MainWindow>> _windows;
    private DispatcherQueueTimer? _debounce;

    public SessionManager(
        SessionStore store,
        ConfigService config,
        DispatcherQueue dispatcher,
        Func<IEnumerable<MainWindow>> windows)
    {
        _store = store;
        _config = config;
        _dispatcher = dispatcher;
        _windows = windows;
    }

    /// <summary>Load and decide whether to restore. Null = launch default window.</summary>
    public SessionState? LoadForRestore()
    {
        if (!SessionGate.ShouldPersist(_config.WindowSaveState))
        {
            // window-save-state=never: drop any stale session.
            _store.Delete();
            return null;
        }
        var state = _store.Load();
        if (state is null) return null;
        return SessionGate.ShouldRestore(_config.WindowSaveState, state.CleanShutdown)
            ? state
            : null;
    }

    /// <summary>Subscribe a window's change signals to the debounced persist.</summary>
    public void Track(MainWindow window)
    {
        if (window.IsQuickTerminal) return;

        var tm = window.TabManager;
        tm.TabAdded += OnTabAdded;
        tm.TabRemoved += OnLayoutSignal;
        tm.TabMoved += OnTabMoved;
        tm.ActiveTabChanged += OnActiveTabChanged;
        // Pane-level structural changes for the tabs present now, plus any
        // added later (OnTabAdded wires those).
        foreach (var t in tm.Tabs)
            t.PaneHost.LayoutChanged += OnLayoutSignal;
        window.PositionChanged += OnLayoutSignal;
    }

    private void OnTabAdded(object? sender, Ghostty.Core.Tabs.TabModel tab)
    {
        tab.PaneHost.LayoutChanged += OnLayoutSignal;
        RequestPersist();
    }

    private void OnTabMoved(object? sender, (Ghostty.Core.Tabs.TabModel tab, int from, int to) e)
        => RequestPersist();

    private void OnActiveTabChanged(object? sender, Ghostty.Core.Tabs.TabModel tab)
        => RequestPersist();

    private void OnLayoutSignal(object? sender, EventArgs e) => RequestPersist();
    private void OnLayoutSignal(object? sender, Ghostty.Core.Tabs.TabModel tab) => RequestPersist();

    public void RequestPersist()
    {
        if (!SessionGate.ShouldPersist(_config.WindowSaveState)) return;
        _debounce ??= _dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(DebounceMs);
        _debounce.IsRepeating = false;
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Start();
    }

    private void OnDebounceTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        PersistLiveWindows();
    }

    /// <summary>
    /// Capture every live window and write with CleanShutdown=false. The
    /// running file therefore always reflects the set of open windows
    /// (debounced), which is what `always` restores after a crash.
    /// </summary>
    public void PersistLiveWindows()
    {
        if (!SessionGate.ShouldPersist(_config.WindowSaveState)) return;
        var state = new SessionState { CleanShutdown = false };
        foreach (var w in _windows())
        {
            var ws = w.CaptureSession();
            if (ws is not null && ws.Tabs.Count > 0) state.Windows.Add(ws);
        }
        // Nothing to save mid-session: skip rather than blank the file.
        if (state.Windows.Count == 0) return;
        _store.Save(state);
    }

    /// <summary>
    /// Mark the session clean at app shutdown. The running file already
    /// reflects the open windows (continuously persisted), so we re-read
    /// it and flip the flag rather than recapture -- by the time the last
    /// window closes, earlier windows have already left the live set.
    /// <paramref name="closingFallback"/> covers the cold case where no
    /// mid-session write happened (e.g. launch then immediate quit): we
    /// capture the last window directly. Windows close within the 750 ms
    /// debounce of one another on a multi-window quit, so the pre-quit
    /// snapshot still holds them all.
    /// </summary>
    public void FinalizeCleanShutdown(MainWindow? closingFallback)
    {
        _debounce?.Stop();
        if (!SessionGate.ShouldPersist(_config.WindowSaveState)) return;

        var onDisk = _store.Load();
        if (onDisk is { Windows.Count: > 0 })
        {
            onDisk.CleanShutdown = true;
            _store.Save(onDisk);
            return;
        }

        // Cold path: nothing persisted this run. Capture the last window.
        var state = new SessionState { CleanShutdown = true };
        var ws = closingFallback?.CaptureSession();
        if (ws is not null && ws.Tabs.Count > 0)
        {
            state.Windows.Add(ws);
            _store.Save(state);
        }
    }
}
