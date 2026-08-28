using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The single translation point between <see cref="TabManager"/> state
/// and the row sequence a strip renders. Both hosts take their order
/// from here, so the horizontal TabView and the vertical NavigationView
/// agree on it structurally rather than coincidentally.
///
/// Why hosts read state instead of event indices:
/// <see cref="TabManager.TabMoved"/> carries the raw operation's indices,
/// and <c>Normalize</c> may relocate tabs afterwards (a move that pulls a
/// member out mid-run re-gathers its group). A listener replaying event
/// indices strands desynced; the projector reads the manager's FINAL
/// state and describes what brings any strip back to it. Until the
/// groups slice lands (headers, collapsed chips) the projection is the
/// flat list both strips already rendered, which is what makes this
/// seam behavior-neutral.
/// </summary>
internal static class TabStripProjection
{
    /// <summary>
    /// The row order a strip renders for the manager's current state.
    /// A fresh list every call: callers may rebuild their containers
    /// while walking it, and the projection must not move under them.
    /// </summary>
    public static IReadOnlyList<TabModel> Rows(TabManager manager)
    {
        var rows = new List<TabModel>(manager.Tabs.Count);
        foreach (var tab in manager.Tabs) rows.Add(tab);
        return rows;
    }

    /// <summary>
    /// One repair instruction: take <see cref="Tab"/> out of wherever it
    /// currently sits and insert it at <see cref="To"/>. The index counts
    /// the collection's state AFTER the previous ops applied, so ops are
    /// applied in order against the live container.
    /// </summary>
    public readonly record struct RowMove(TabModel Tab, int To);

    /// <summary>
    /// The moves that bring <paramref name="current"/> into
    /// <paramref name="desired"/> order. Both lists must hold the same
    /// tabs; only the order may differ. Rows already in place emit no op:
    /// re-inserting an item at the index it already occupies is a
    /// collection mutation with no effect, and once order is semantic it
    /// is a second writer fighting the control's own reorder (the churn
    /// the TabView sync audit flagged).
    /// </summary>
    public static IReadOnlyList<RowMove> Diff(
        IReadOnlyList<TabModel> desired, IReadOnlyList<TabModel> current)
    {
        if (desired.Count != current.Count)
            throw new InvalidOperationException(
                "TabStripProjection.Diff: the strip and the manager hold " +
                "different tab counts. Order is repairable; membership skew " +
                "is a wiring bug, not a projection.");
        // Left-to-right placement: every row the two lists already agree
        // on at the front stays untouched, so an ordinary single move
        // repairs with exactly one op and nothing else is disturbed.
        var working = new List<TabModel>(current);
        var ops = new List<RowMove>();
        for (int i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            int at = working.IndexOf(want);
            if (at < 0)
                throw new InvalidOperationException(
                    "TabStripProjection.Diff: the strip is missing a tab the " +
                    "manager holds. Membership skew is a wiring bug, not a " +
                    "projection.");
            if (at == i) continue;
            working.RemoveAt(at);
            working.Insert(i, want);
            ops.Add(new RowMove(want, i));
        }
        return ops;
    }
}
