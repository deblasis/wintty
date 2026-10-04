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

    // Every window this run captured, keyed by the window itself. The
    // debounced persist writes it (the file IS this set from then on) and
    // each window's own close refreshes its entry, so the clean-shutdown
    // write at app exit has the closing window's real state to write
    // rather than whatever the last debounce happened to see. See
    // SessionWindowLedger for the two rules it keeps.
    private readonly SessionWindowLedger<MainWindow> _ledger = new();

    // A cold `wintty -e` holds the saved session: it was not restored, so it
    // must not be written either (#1136).
    private bool _held;

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
    /// A cold <c>wintty -e</c> opens only its command's window and restores
    /// nothing (#1136). Until <see cref="ReleaseHeldSession"/>, this process
    /// writes no session at all, neither the debounced save nor the
    /// clean-shutdown mark, so the saved session stays exactly as it is on
    /// disk for the next plain launch, whatever the user does in the -e
    /// window.
    /// <para>
    /// The hold must end the moment the process opens any other window
    /// (App.RestoreHeldSession, called by every window opener): a window
    /// opened while it lasts would otherwise never be saved at all.
    /// </para>
    /// </summary>
    public void HoldForLaunchCommand() => _held = true;

    /// <summary>Whether the saved session is being held (see above).</summary>
    public bool SessionHeld => _held;

    /// <summary>
    /// The first plain launch forwarded into a held process restores the
    /// session: this ends the hold and answers what a cold launch's
    /// <see cref="LoadForRestore"/> would have, and saving resumes normally.
    /// </summary>
    public SessionState? ReleaseHeldSession()
    {
        if (!_held) return null;
        _held = false;
        return LoadForRestore();
    }

    /// <summary>Subscribe a window's change signals to the debounced persist.</summary>
    public void Track(MainWindow window)
    {
        if (window.IsQuickTerminal) return;

        var tm = window.TabManager;
        tm.TabAdded += OnTabAdded;
        tm.TabRemoved += OnTabRemoved;
        tm.TabMoved += OnTabMoved;
        tm.ActiveTabChanged += OnActiveTabChanged;
        // The group ops raised nothing at all: a rename, a recolor and a
        // collapse write TabGroup's plain INPC properties, and a dissolve
        // removes the group from the manager's registry. Neither is a tab
        // add/move/activation, so without this the file kept whatever the
        // registry looked like when the tab list last changed.
        tm.GroupsChanged += OnGroupsChanged;
        // Pane-level structural changes for the tabs present now, plus any
        // added later (OnTabAdded wires those).
        foreach (var t in tm.Tabs)
            WatchTab(t);
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
        tm.GroupsChanged -= OnGroupsChanged;
        foreach (var t in tm.Tabs)
            UnwatchTab(t);
        window.PositionChanged -= OnLayoutSignal;
    }

    private void OnTabAdded(object? sender, Ghostty.Core.Tabs.TabModel tab)
    {
        WatchTab(tab);
        RequestPersist();
    }

    private void OnTabRemoved(object? sender, Ghostty.Core.Tabs.TabModel tab)
    {
        // Unhook the closed tab's pane host so a per-tab subscription does not
        // outlive the tab within a still-open window.
        UnwatchTab(tab);
        RequestPersist();
    }

    /// <summary>
    /// The per-tab signals that name WHAT the tab is rather than how its
    /// panes are arranged.
    ///
    /// The rename, the pin and the group membership ride TabModel's own
    /// INPC, which is exact: each raises once per actual change, so this
    /// is not a second copy of the title stream the caption follows. The
    /// directory rides the pane host instead of the tab, because the tab's
    /// copy is derived and one shell reporting the same folder again would
    /// otherwise cost a write; and it is the leaf's own <c>LastCwd</c> that
    /// session restore spawns into (Ghostty.Core/Session/SessionTree.cs),
    /// so a cd that moves the tab's label and not that leaf is the half
    /// that had no persist signal at all.
    /// </summary>
    private void WatchTab(Ghostty.Core.Tabs.TabModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;
        tab.PaneHost.CwdChanged += OnCwdChanged;
        tab.PaneHost.LayoutChanged += OnLayoutSignal;
    }

    private void UnwatchTab(Ghostty.Core.Tabs.TabModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.PaneHost.CwdChanged -= OnCwdChanged;
        tab.PaneHost.LayoutChanged -= OnLayoutSignal;
    }

    /// <summary>
    /// The tab properties the saved session reads back. Everything else
    /// TabModel raises is either not saved (bell, idle, colour -- all
    /// documented in-memory) or already covered by a stronger signal: the
    /// derived title names are re-raised for every tier that moves the
    /// label, and the shell-reported title and directory change on every
    /// prompt of every shell, so persisting on those would turn the debounce
    /// into a constant write.
    /// </summary>
    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Ghostty.Core.Tabs.TabModel.UserOverrideTitle):
            case nameof(Ghostty.Core.Tabs.TabModel.IsPinned):
            case nameof(Ghostty.Core.Tabs.TabModel.Group):
                RequestPersist();
                break;
        }
    }

    private void OnCwdChanged(object? sender, string? cwd) => RequestPersist();

    private void OnGroupsChanged(object? sender, EventArgs e) => RequestPersist();

    private void OnTabMoved(object? sender, (Ghostty.Core.Tabs.TabModel tab, int from, int to) e)
        => RequestPersist();

    private void OnActiveTabChanged(object? sender, Ghostty.Core.Tabs.TabModel tab)
        => RequestPersist();

    private void OnLayoutSignal(object? sender, EventArgs e) => RequestPersist();

    public void RequestPersist()
    {
        if (_held) return;
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
        if (_held) return;
        if (!SessionGate.ShouldPersist(_config.WindowSaveState)) return;
        SaveLiveWindows(cleanShutdown: false);
    }

    /// <summary>
    /// Capture every live window, write the set, and leave the ledger
    /// holding exactly what was written. Null (nothing written) when no
    /// window holds a tab: blanking the file would throw away a session
    /// that is still good.
    /// </summary>
    private void SaveLiveWindows(bool cleanShutdown)
    {
        var captured = new List<(MainWindow, Ghostty.Core.Session.WindowSession)>();
        var state = new SessionState { CleanShutdown = cleanShutdown };
        foreach (var w in _windows())
        {
            var ws = w.CaptureSession();
            if (ws is not null && ws.Tabs.Count > 0)
            {
                state.Windows.Add(ws);
                captured.Add((w, ws));
            }
        }
        if (state.Windows.Count == 0) return;
        _ledger.ReplaceAll(captured);
        _store.Save(state);
    }

    /// <summary>
    /// Take one closing window's state while its panes are still alive.
    ///
    /// Called from the window's own teardown, beside the capture it
    /// already makes for reopen-closed-window, and for the same reason:
    /// a window's surfaces are freed a few lines later and its HWND is
    /// gone by the time the app's per-window Closed handler runs, so
    /// recapturing it there reads a torn-down window. The capture replaces
    /// this window's entry in the ledger rather than appending, which is
    /// what makes the clean-shutdown write carry the state the user
    /// actually quit with -- the rename in the last second before the X is
    /// the case that used to be lost.
    /// </summary>
    public void CaptureClosingWindow(MainWindow window, Ghostty.Core.Session.WindowSession? session)
    {
        if (_held) return;
        if (window.IsQuickTerminal) return;
        if (session is not { Tabs.Count: > 0 }) return;
        _ledger.Capture(window, session);
    }

    /// <summary>
    /// Mark the session clean at app shutdown, from the windows as they
    /// were captured rather than from whatever the file happens to hold.
    ///
    /// The closing window is in <see cref="_ledger"/> with the capture its
    /// own teardown took, and every window that closed earlier in the quit
    /// cascade is in there too -- which is why this writes the ledger
    /// instead of re-reading the file. The old shape (load, flip the flag,
    /// save) could only ever mark the last debounced write clean, so a
    /// rename, a pin, a group edit or a cd made inside the debounce window
    /// was saved as unclean-but-stale and restored as the old value.
    /// <para>
    /// An empty ledger means nothing was ever captured this run, which is
    /// the cold path: a launch that was told not to restore, or one whose
    /// close never reached the capture. Then the file is kept as it is and
    /// only marked clean -- <see cref="closingFallback"/> covers the case
    /// where even that is empty, and we capture the last window directly.
    /// </para>
    /// </summary>
    public void FinalizeCleanShutdown(MainWindow? closingFallback)
    {
        _debounce?.Stop();
        if (_held) return;
        if (!SessionGate.ShouldPersist(_config.WindowSaveState)) return;

        var captured = _ledger.Sessions;
        if (captured.Count > 0)
        {
            var state = new SessionState { CleanShutdown = true };
            state.Windows.AddRange(captured);
            _store.Save(state);
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
        var cold = new SessionState { CleanShutdown = true };
        var ws = closingFallback?.CaptureSession();
        if (ws is not null && ws.Tabs.Count > 0)
        {
            cold.Windows.Add(ws);
            _store.Save(cold);
        }
    }
}
