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

    /// <summary>
    /// One row a strip renders, group-aware: a group's header, or a tab
    /// row (grouped or not -- membership is the tab's own
    /// <see cref="TabModel.Group"/>).
    /// </summary>
    public abstract record ProjectedRow
    {
        public sealed record Header(TabGroup Group) : ProjectedRow;

        public sealed record Item(TabModel Tab) : ProjectedRow;
    }

    /// <summary>
    /// The rows a strip renders for the manager's current state, groups
    /// included. Order is <see cref="TabManager.Tabs"/> order with a
    /// header in front of each run. Collapse hides members EXCEPT the
    /// active one -- the Edge-135 rule (2.9 row 14) that keeps selection
    /// never hidden -- so a collapsed group projects as its header plus
    /// the active member's row, or the header alone when the run holds no
    /// active tab: the fully-collapsed shape the vertical strip renders
    /// as a childless header and the horizontal strip renders as a chip.
    /// Activating a different member of a collapsed group swaps which
    /// member survives here and nothing else; the collapse bit is never
    /// touched by a projection.
    ///
    /// Contiguity (a manager invariant) is what puts each header in front
    /// of all of its members; this walk does not re-order anything.
    ///
    /// The collapsed-with-active shape above is VERTICAL's. Horizontal
    /// lowers these same rows -- see <see cref="HorizontalRows"/> -- so
    /// the two strips read one walk and cannot disagree on what is
    /// visible.
    /// </summary>
    public static IReadOnlyList<ProjectedRow> GroupedRows(TabManager manager)
    {
        var rows = new List<ProjectedRow>(manager.Tabs.Count);
        var headered = new HashSet<TabGroup>();
        foreach (var tab in manager.Tabs)
        {
            if (tab.Group is { } group)
            {
                if (headered.Add(group))
                    rows.Add(new ProjectedRow.Header(group));
                if (!group.IsCollapsed || ReferenceEquals(tab, manager.ActiveTab))
                    rows.Add(new ProjectedRow.Item(tab));
            }
            else
            {
                rows.Add(new ProjectedRow.Item(tab));
            }
        }
        return rows;
    }

    /// <summary>
    /// One row the horizontal strip renders: the <see cref="GroupedRows"/>
    /// sequence with each header lowered to the shape the strip draws.
    /// </summary>
    public abstract record HorizontalRow
    {
        /// <summary>
        /// A collapsed run that does not hold the active tab, rendered as
        /// ONE item. Its members are hidden and reachable only by
        /// expanding.
        /// </summary>
        public sealed record Chip(TabGroup Group) : HorizontalRow;

        /// <summary>
        /// A tab rendered as itself: ungrouped, pinned, a member of an
        /// expanded run, or the active member of a collapsed run.
        /// </summary>
        public sealed record Item(TabModel Tab) : HorizontalRow;
    }

    /// <summary>
    /// The horizontal strip's reading. An expanded run contributes its
    /// members alone -- the strip draws no header rows, and the run label
    /// names a run the strip itself does not draw. A collapsed run
    /// contributes one chip, except when the run holds the active tab:
    /// the walk already projects that member as an item, and a chip
    /// beside it would draw the same run twice, so the chip is suppressed
    /// and the run reads as its member.
    ///
    /// The suppression is the reading's one deliberate loss: the other
    /// members of an active-holding collapsed run appear nowhere in these
    /// rows. That is why <see cref="ModelIndexToVisibleIndex"/> answers
    /// -1 for them rather than a slot, and why the expansion invariant
    /// these rows are tested under holds per chip'd run.
    /// </summary>
    public static IReadOnlyList<HorizontalRow> HorizontalRows(TabManager manager)
    {
        var rows = new List<HorizontalRow>(manager.Tabs.Count);
        foreach (var projected in GroupedRows(manager))
        {
            switch (projected)
            {
                case ProjectedRow.Header { Group: { } group }:
                    // The walk keeps the active member visible under its
                    // header, so a chip here would show the run twice.
                    if (group.IsCollapsed
                        && !ReferenceEquals(manager.ActiveTab?.Group, group))
                        rows.Add(new HorizontalRow.Chip(group));
                    break;
                case ProjectedRow.Item { Tab: { } tab }:
                    rows.Add(new HorizontalRow.Item(tab));
                    break;
            }
        }
        return rows;
    }

    /// <summary>
    /// Manager index of the tab rendered at visible slot
    /// <paramref name="visibleIndex"/>, or -1 when the slot renders a chip
    /// or is out of range. Chips are slots -- the strip's item collection
    /// holds them -- so no TabItems index may be compared with a manager
    /// index without crossing here first. The caller routes a -1 to the
    /// slot's group instead: selecting a chip expands, dropping on a chip
    /// joins.
    /// </summary>
    public static int VisibleIndexToModelIndex(TabManager manager, int visibleIndex)
    {
        var slot = 0;
        foreach (var row in HorizontalRows(manager))
        {
            if (slot == visibleIndex)
                return row is HorizontalRow.Item { Tab: { } tab }
                    ? manager.IndexOf(tab) : -1;
            slot++;
        }
        return -1;
    }

    /// <summary>
    /// The group whose chip renders at visible slot
    /// <paramref name="visibleIndex"/>, or null when the slot renders a
    /// tab row or does not exist. The complement of
    /// <see cref="VisibleIndexToModelIndex"/>'s -1, which its own doc
    /// promises to route to "the slot's group": that answer says a chip
    /// took the slot without saying which, and the drop-at-a-run fork
    /// needs it said. Read through the projection because the members
    /// were hidden at drop time -- a TabItems index cannot say which run
    /// took the drop when the strip's own order has already been
    /// reordered under it.
    /// </summary>
    public static TabGroup? VisibleGroupAt(TabManager manager, int visibleIndex)
    {
        var slot = 0;
        foreach (var row in HorizontalRows(manager))
        {
            if (slot == visibleIndex)
                return row is HorizontalRow.Chip { Group: { } group } ? group : null;
            slot++;
        }
        return null;
    }

    /// <summary>
    /// Visible slot of the tab at manager index
    /// <paramref name="modelIndex"/>, or -1 when the index is out of range
    /// or the tab has no slot: a member hidden by a chip'd run renders
    /// nowhere, so there is nothing to select, bridge, or reorder for it.
    /// Chips occupy slots, so every slot past a chip is one further along
    /// than the manager index suggests -- the round trip through
    /// <see cref="VisibleIndexToModelIndex"/> is identity in both
    /// directions on exactly the slots and indices that exist, which the
    /// tests pin by execution.
    /// </summary>
    public static int ModelIndexToVisibleIndex(TabManager manager, int modelIndex)
    {
        if (modelIndex < 0 || modelIndex >= manager.Tabs.Count) return -1;
        var tab = manager.Tabs[modelIndex];
        var slot = 0;
        foreach (var row in HorizontalRows(manager))
        {
            if (row is HorizontalRow.Item { Tab: { } candidate }
                && ReferenceEquals(candidate, tab))
                return slot;
            slot++;
        }
        return -1;
    }
}
