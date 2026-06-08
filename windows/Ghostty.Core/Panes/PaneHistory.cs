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

    // Only consecutive resizes WITHIN this window coalesce, so a held-chord
    // burst collapses to one undo step while two deliberate, seconds-apart
    // resizes stay independently undoable.
    private static readonly TimeSpan ResizeCoalesceWindow = TimeSpan.FromMilliseconds(750);

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

    /// <summary>
    /// Record the pre-op state and clear the redo stack (a new op
    /// invalidates redo). Consecutive resizes within
    /// <see cref="ResizeCoalesceWindow"/> coalesce into the earliest
    /// pre-burst snapshot.
    ///
    /// Returns the leaves orphaned by clearing redo: a redo entry can be
    /// the SOLE reference keeping a closed/split-away pane's surface alive
    /// (e.g. Split then Undo leaves the new pane reachable only via redo).
    /// Dropping it here without disposal would leak the libghostty surface
    /// and its shell forever, so the caller must tear those leaves down
    /// (excluding any still in the live tree).
    /// </summary>
    public IReadOnlyList<LeafPane> Push(PaneSnapshot pre)
    {
        var now = _time.GetUtcNow();
        var clearedRedo = _redo.Count > 0 ? new List<Entry>(_redo) : null;

        if (pre.Kind == PaneOpKind.Resize
            && _undo.Count > 0
            && _undo[^1].Snapshot.Kind == PaneOpKind.Resize
            && now - _undo[^1].Stamp < ResizeCoalesceWindow)
        {
            // Keep the earlier (pre-burst) snapshot; just refresh its
            // timestamp so the whole burst expires from the last nudge.
            _undo[^1] = _undo[^1] with { Stamp = now };
        }
        else
        {
            _undo.Add(new Entry(pre, now));
        }

        _redo.Clear();
        return clearedRedo is null ? Array.Empty<LeafPane>() : OrphansOf(clearedRedo);
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

    /// <summary>
    /// Drop entries (from either stack) whose age exceeds the timeout.
    /// Returns the leaves that were referenced ONLY by evicted entries
    /// (candidates for surface disposal). The caller must still exclude
    /// any leaf present in the live tree before tearing it down.
    /// </summary>
    public IReadOnlyList<LeafPane> Prune(DateTimeOffset now)
    {
        var evicted = new List<Entry>();
        RemoveExpired(_undo, now, evicted);
        RemoveExpired(_redo, now, evicted);
        if (evicted.Count == 0) return Array.Empty<LeafPane>();
        return OrphansOf(evicted);
    }

    /// <summary>Drop everything; return every leaf any entry referenced.</summary>
    public IReadOnlyList<LeafPane> Clear()
    {
        var all = _undo.Concat(_redo).ToList();
        _undo.Clear();
        _redo.Clear();
        if (all.Count == 0) return Array.Empty<LeafPane>();
        return LeavesOf(all).Distinct().ToList();
    }

    private void RemoveExpired(List<Entry> stack, DateTimeOffset now, List<Entry> evicted)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (now - stack[i].Stamp < _timeout) continue;
            evicted.Add(stack[i]);
            stack.RemoveAt(i);
        }
    }

    // Leaves in evicted entries that no surviving entry still references.
    private IReadOnlyList<LeafPane> OrphansOf(List<Entry> evicted)
    {
        var survivors = LeavesOf(_undo.Concat(_redo)).ToHashSet();
        return LeavesOf(evicted)
            .Where(l => !survivors.Contains(l))
            .Distinct()
            .ToList();
    }

    private static IEnumerable<LeafPane> LeavesOf(IEnumerable<Entry> entries)
        => entries.SelectMany(e => PaneTree.Leaves(e.Snapshot.Root));
}
