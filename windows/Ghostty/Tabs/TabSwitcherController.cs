using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;

namespace Ghostty.Tabs;

/// <summary>
/// Drives the Ctrl+Tab MRU cycle. A cycle begins on the first
/// <see cref="StartOrAdvance"/>: the manager's current MRU order is frozen into
/// a <see cref="TabCycleSession{T}"/> and the popup is shown. Subsequent calls
/// advance the cursor. <see cref="Commit"/> activates the highlighted tab (which
/// is when MRU order actually changes); <see cref="Cancel"/> leaves the active
/// tab untouched. Owned by <see cref="MainWindow"/>; one per window.
/// </summary>
internal sealed class TabSwitcherController
{
    private readonly TabManager _manager;
    private TabCycleSession<TabModel>? _session;

    public TabSwitcherController(TabManager manager) => _manager = manager;

    public bool IsCycling => _session is not null;

    /// <summary>
    /// Raised when a cycle starts so the host can show and populate the popup.
    /// Carries the frozen candidate order (most-recent first).
    /// </summary>
    public event EventHandler<IReadOnlyList<TabModel>>? Started;

    /// <summary>Raised each time the highlight moves (and on start).</summary>
    public event EventHandler<TabModel>? HighlightChanged;

    /// <summary>Raised when the cycle ends (commit or cancel) so the host hides the popup.</summary>
    public event EventHandler? Ended;

    public void StartOrAdvance(bool forward)
    {
        if (_session is null)
        {
            // Snapshot a COPY so later activations cannot mutate the frozen
            // order mid-cycle.
            var frozen = _manager.MruOrder.ToList();
            if (frozen.Count <= 1) return; // nothing to switch to
            _session = new TabCycleSession<TabModel>(frozen);
            Started?.Invoke(this, frozen);
        }

        var highlight = _session.Advance(forward);
        HighlightChanged?.Invoke(this, highlight);
    }

    public void Commit()
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        Ended?.Invoke(this, EventArgs.Empty);

        // Activate only if the highlighted tab still exists (it may have been
        // closed mid-cycle). TabManager.Activate already ignores non-members,
        // but guard explicitly so a stale highlight is a clean no-op.
        var target = session.Current;
        if (_manager.Tabs.Contains(target))
            _manager.Activate(target);
    }

    public void Cancel()
    {
        if (_session is null) return;
        _session = null;
        Ended?.Invoke(this, EventArgs.Empty);
    }
}
