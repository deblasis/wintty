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

    // A cold `wintty -e` skipped the restore, so the file on disk is still
    // the previous session, untouched by this process (#1136).
    private bool _restoreSkipped;

    // This process has written the session at least once.
    private bool _wroteThisRun;

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
        if (!SessionGate.ShouldRestore(_config.WindowSaveState, state.CleanShutdown))
            return null;

        // Arm the dirty flag: rewrite the restored set as not-clean now, so a
        // crash later this run does not leave the previous clean snapshot on
        // disk for `default` to wrongly restore. Uses the on-disk windows (live
        // windows are not yet registered at startup). A normal clean exit
        // re-marks it clean via FinalizeCleanShutdown.
        state.CleanShutdown = false;
        _store.Save(state);
        return state;
    }

    /// <summary>
    /// A cold <c>wintty -e</c> skipped the restore (#1136). Its own window is
    /// <see cref="MainWindow.ExcludedFromSession"/>, so the writes below
    /// leave it out; this records that the file on disk is still the
    /// previous session, which the clean-shutdown mark must then leave alone
    /// unless this process saved windows of its own. The process keeps
    /// saving every other window it opens: in the default single-instance
    /// mode it becomes the primary that later launches are forwarded to.
    /// </summary>
    public void NoteRestoreSkipped() => _restoreSkipped = true;

    /// <summary>Subscribe a window's change signals to the debounced persist.</summary>
    public void Track(MainWindow window)
    {
        if (window.IsQuickTerminal) return;

        var tm = window.TabManager;
        tm.TabAdded += OnTabAdded;
        tm.TabRemoved += OnTabRemoved;
        tm.TabMoved += OnTabMoved;
        tm.ActiveTabChanged += OnActiveTabChanged;
        // Pane-level structural changes for the tabs present now, plus any
        // added later (OnTabAdded wires those).
        foreach (var t in tm.Tabs)
            t.PaneHost.LayoutChanged += OnLayoutSignal;
        window.PositionChanged += OnLayoutSignal;
    }

    /// <summary>
    /// Mirror of <see cref="Track"/>: detach every handler when a window
    /// closes. Defensive hygiene -- the dead window would not fire these, but
    /// leaving subscriptions on the app-lifetime manager is a smell.
    /// </summary>
    public void Untrack(MainWindow window)
    {
        if (window.IsQuickTerminal) return;

        var tm = window.TabManager;
        tm.TabAdded -= OnTabAdded;
        tm.TabRemoved -= OnTabRemoved;
        tm.TabMoved -= OnTabMoved;
        tm.ActiveTabChanged -= OnActiveTabChanged;
        foreach (var t in tm.Tabs)
            t.PaneHost.LayoutChanged -= OnLayoutSignal;
        window.PositionChanged -= OnLayoutSignal;
    }

    private void OnTabAdded(object? sender, Ghostty.Core.Tabs.TabModel tab)
    {
        tab.PaneHost.LayoutChanged += OnLayoutSignal;
        RequestPersist();
    }

    private void OnTabRemoved(object? sender, Ghostty.Core.Tabs.TabModel tab)
    {
        // Unhook the closed tab's pane host so a per-tab subscription does not
        // outlive the tab within a still-open window.
        tab.PaneHost.LayoutChanged -= OnLayoutSignal;
        RequestPersist();
    }

    private void OnTabMoved(object? sender, (Ghostty.Core.Tabs.TabModel tab, int from, int to) e)
        => RequestPersist();

    private void OnActiveTabChanged(object? sender, Ghostty.Core.Tabs.TabModel tab)
        => RequestPersist();

    private void OnLayoutSignal(object? sender, EventArgs e) => RequestPersist();

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
            // A cold -e window is a one-off (#1136).
            if (w.ExcludedFromSession) continue;
            var ws = w.CaptureSession();
            if (ws is not null && ws.Tabs.Count > 0) state.Windows.Add(ws);
        }
        // Nothing to save mid-session: skip rather than blank the file. That
        // also covers a -e process with only its -e window open.
        if (state.Windows.Count == 0) return;
        _store.Save(state);
        _wroteThisRun = true;
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

        // The -e window is never the one saved (#1136).
        if (closingFallback is { ExcludedFromSession: true }) closingFallback = null;

        // A -e process that has saved nothing of its own: the file on disk
        // is the previous session. Marking it clean would change what the
        // next launch restores, so it is left alone, unless the window
        // closing now is a real one this process served (a forwarded launch
        // that quit inside the debounce), which is saved as the session.
        if (_restoreSkipped && !_wroteThisRun)
        {
            var own = closingFallback?.CaptureSession();
            if (own is not null && own.Tabs.Count > 0)
                _store.Save(new SessionState { CleanShutdown = true, Windows = { own } });
            return;
        }

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
