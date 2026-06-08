using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Panes;

/// <summary>
/// Bounded undo/redo history of <see cref="PaneSnapshot"/>s for one
/// <c>PaneHost</c>. Pure and host-free: time comes from an injected
/// <see cref="TimeProvider"/> so eviction is deterministic in tests.
///
/// Snapshots are captured BEFORE a mutation. Undo swaps the current
/// state onto the redo stack and returns the previous snapshot; redo is
/// the mirror. A new push clears redo. Consecutive resize ops coalesce
/// so a burst of divider nudges collapses to one undo step.
///
/// The history never disposes surfaces. <see cref="Prune"/>/<see cref="Clear"/>
/// return leaves no longer referenced by any remaining entry; the host
/// intersects that with the live tree before tearing surfaces down.
/// </summary>
internal sealed class PaneHistory
{
    private readonly record struct Entry(PaneSnapshot Snapshot, DateTimeOffset Stamp);

    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly List<Entry> _undo = new(); // top == last
    private readonly List<Entry> _redo = new();

    public PaneHistory(TimeProvider time, TimeSpan timeout)
    {
        _time = time;
        _timeout = timeout;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Record the pre-op state. Clears redo. Coalesces a
    /// resize that immediately follows another resize.</summary>
    public void Push(PaneSnapshot pre)
    {
        if (pre.Kind == PaneOpKind.Resize
            && _undo.Count > 0
            && _undo[^1].Snapshot.Kind == PaneOpKind.Resize)
        {
            // Keep the earlier (pre-burst) snapshot; just refresh its
            // timestamp so the whole burst expires from the last nudge.
            _undo[^1] = _undo[^1] with { Stamp = _time.GetUtcNow() };
            _redo.Clear();
            return;
        }

        _undo.Add(new Entry(pre, _time.GetUtcNow()));
        _redo.Clear();
    }

    /// <summary>Pop the last undo entry; push <paramref name="current"/>
    /// onto redo. Returns null when there is nothing to undo.</summary>
    public PaneSnapshot? Undo(PaneSnapshot current)
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new Entry(current, _time.GetUtcNow()));
        return entry.Snapshot;
    }

    /// <summary>Mirror of <see cref="Undo"/>.</summary>
    public PaneSnapshot? Redo(PaneSnapshot current)
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new Entry(current, _time.GetUtcNow()));
        return entry.Snapshot;
    }

    // Eviction added in Task 4.
}
