using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The projector is the single translation point between the manager's
/// order and what a strip renders, so these tests drive it the way the
/// hosts do: manager mutations on one side, a live collection replaying
/// the shell's remove-then-insert discipline on the other.
///
/// The seams here are the ones PR 2 owns (spec 10): raw TabMoved
/// indices that Normalize has since repaired, a Move the invariants
/// clamp to a silent no-op, and the batched TabMoved pairs of a run
/// move. The shell cannot load into this host, so the mirror stands in
/// for TabItems; TabStripSyncWiringTests pins the real handlers.
/// </summary>
public class TabStripProjectionTests
{
    private static TabManager NewManager(int extraTabs)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 0; i < extraTabs; i++) mgr.NewTab();
        return mgr;
    }

    /// <summary>A live strip, mutated only by the hosts' discipline.</summary>
    private sealed class Strip
    {
        public readonly List<TabModel> Items = new();

        public Strip(IEnumerable<TabModel> seed) => Items.AddRange(seed);

        // TabHost-style replay of one TabMoved payload: take the item
        // out, insert it at the event's raw index.
        public void Replay(TabModel tab, int to)
        {
            Items.Remove(tab);
            Items.Insert(to, tab);
        }

        // The projector's ops, applied the way both hosts apply them.
        public void Apply(IReadOnlyList<TabStripProjection.RowMove> ops)
        {
            foreach (var op in ops)
            {
                Items.Remove(op.Tab);
                Items.Insert(op.To, op.Tab);
            }
        }
    }

    /// <summary>
    /// OnTabDragCompleted's shape, driven against a live mirror: read the
    /// strip index, Move in manager space, reconcile via the projector.
    /// </summary>
    private static void CompleteDrag(TabManager mgr, Strip strip, TabModel dragged)
    {
        int newIndex = strip.Items.IndexOf(dragged);
        int oldIndex = mgr.IndexOf(dragged);
        if (oldIndex != newIndex && oldIndex >= 0)
            mgr.Move(oldIndex, newIndex);
        Reconcile(mgr, strip);
    }

    // The repair step both hosts run after any strip-side change.
    private static void Reconcile(TabManager mgr, Strip strip)
        => strip.Apply(TabStripProjection.Diff(TabStripProjection.Rows(mgr), strip.Items));

    private static int ManagerMutations(TabManager mgr, Action action)
    {
        int count = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => count++;
        mgr.Tabs.CollectionChanged += handler;
        try { action(); } finally { mgr.Tabs.CollectionChanged -= handler; }
        return count;
    }

    // --- Rows ---

    [Fact]
    public void Rows_are_the_manager_order_while_headers_are_off()
    {
        var mgr = NewManager(2); // [A, B, C]
        var expected = mgr.Tabs.ToArray();

        Assert.Equal(expected, TabStripProjection.Rows(mgr));
    }

    [Fact]
    public void Rows_is_a_snapshot_that_survives_a_later_reorder()
    {
        var mgr = NewManager(1); // [A, B, C]
        var rows = TabStripProjection.Rows(mgr);
        var expected = rows.ToArray();

        mgr.Move(0, 2);

        Assert.Equal(expected, rows); // the projection did not move under its reader
    }

    // --- GroupedRows ---

    private static List<TabModel> ItemsOf(IReadOnlyList<TabStripProjection.ProjectedRow> rows)
    {
        var items = new List<TabModel>(rows.Count);
        foreach (var row in rows)
            if (row is TabStripProjection.ProjectedRow.Item item)
                items.Add(item.Tab);
        return items;
    }

    [Fact]
    public void GroupedRows_interleaves_headers_and_expands_back_to_tabs()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        mgr.SetPinned(mgr.Tabs[0], true);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);

        var rows = TabStripProjection.GroupedRows(mgr);

        // Pin prefix first, then the header in front of its run, then the
        // ungrouped tail -- and the items, read straight off, are Tabs.
        Assert.Collection(rows,
            r => Assert.Equal(mgr.Tabs[0], Assert.IsType<TabStripProjection.ProjectedRow.Item>(r).Tab),
            r => Assert.Equal(group, Assert.IsType<TabStripProjection.ProjectedRow.Header>(r).Group),
            r => Assert.Equal(mgr.Tabs[1], Assert.IsType<TabStripProjection.ProjectedRow.Item>(r).Tab),
            r => Assert.Equal(mgr.Tabs[2], Assert.IsType<TabStripProjection.ProjectedRow.Item>(r).Tab),
            r => Assert.Equal(mgr.Tabs[3], Assert.IsType<TabStripProjection.ProjectedRow.Item>(r).Tab));
        Assert.Equal(mgr.Tabs.ToArray(), ItemsOf(rows));
    }

    [Fact]
    public void A_collapsed_group_hides_every_member_except_the_active_one()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.Activate(mgr.Tabs[2]);

        mgr.CollapseGroup(group, true);
        var rows = TabStripProjection.GroupedRows(mgr);

        // The Edge-135 rule at the seam the strips project from: the
        // header stays, the active member's row stays under it, the
        // inactive member is gone. The ungrouped leading tab renders on.
        Assert.Equal(new[] { mgr.Tabs[0], mgr.Tabs[2] }, ItemsOf(rows));
        Assert.IsType<TabStripProjection.ProjectedRow.Header>(rows[1]);
    }

    [Fact]
    public void A_collapsed_group_without_the_active_member_projects_header_only()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.Activate(mgr.Tabs[0]); // the active tab sits outside the run

        mgr.CollapseGroup(group, true);
        var rows = TabStripProjection.GroupedRows(mgr);

        // The fully-collapsed shape: vertical renders a childless header,
        // horizontal renders its chip from the same rows. Only the
        // ungrouped tab survives beside it.
        Assert.Equal(2, rows.Count);
        Assert.Equal(group,
            Assert.IsType<TabStripProjection.ProjectedRow.Header>(rows[1]).Group);
        Assert.Equal(new[] { mgr.Tabs[0] }, ItemsOf(rows));
    }

    [Fact]
    public void Activating_across_a_collapsed_group_swaps_the_visible_member()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.CollapseGroup(group, true);

        mgr.Activate(mgr.Tabs[1]);
        Assert.Equal(new[] { mgr.Tabs[0], mgr.Tabs[1] },
            ItemsOf(TabStripProjection.GroupedRows(mgr)));
        Assert.True(group.IsCollapsed);

        mgr.Activate(mgr.Tabs[2]);
        Assert.Equal(new[] { mgr.Tabs[0], mgr.Tabs[2] },
            ItemsOf(TabStripProjection.GroupedRows(mgr)));
        Assert.True(group.IsCollapsed); // no accordion: the bit never moved
    }

    // --- Diff ---

    [Fact]
    public void Diff_of_matching_orders_is_empty()
    {
        var mgr = NewManager(2);
        var rows = TabStripProjection.Rows(mgr);

        Assert.Empty(TabStripProjection.Diff(rows, new List<TabModel>(rows)));
    }

    [Fact]
    public void Diff_of_an_order_shifted_behind_its_target_is_one_op()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var desired = new[] { mgr.Tabs[0], mgr.Tabs[2], mgr.Tabs[1], mgr.Tabs[3] };

        var ops = TabStripProjection.Diff(desired, new List<TabModel>(mgr.Tabs));

        var op = Assert.Single(ops);
        Assert.Equal(mgr.Tabs[2], op.Tab); // C moves forward; nothing else is disturbed
        Assert.Equal(1, op.To);
    }

    [Fact]
    public void Diff_of_a_full_reversal_takes_one_op_per_row_but_the_last()
    {
        var mgr = NewManager(2); // [A, B, C]
        var desired = new[] { mgr.Tabs[2], mgr.Tabs[1], mgr.Tabs[0] };

        Assert.Equal(2, TabStripProjection.Diff(desired, new List<TabModel>(mgr.Tabs)).Count);
    }

    [Fact]
    public void Diff_refuses_membership_skew_instead_of_repairing_past_it()
    {
        var mgr = NewManager(2);
        var other = NewManager(0);

        Assert.Throws<InvalidOperationException>(
            () => TabStripProjection.Diff(mgr.Tabs, new List<TabModel> { mgr.Tabs[0] }));
        Assert.Throws<InvalidOperationException>(
            () => TabStripProjection.Diff(mgr.Tabs, new List<TabModel>(other.Tabs)));
    }

    // --- Seam a: TabMoved reports raw op indices; Normalize repairs after ---

    [Fact]
    public void A_move_normalize_repairs_strands_a_raw_replay_and_the_projector_converges_it()
    {
        var mgr = NewManager(3); // [A, B, C, X]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[1], mgr.Tabs[2] }, group);
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var x = mgr.Tabs[3];
        var strip = new Strip(mgr.Tabs);
        var moves = new List<(TabModel tab, int from, int to)>();
        mgr.TabMoved += (_, e) => { moves.Add(e); strip.Replay(e.tab, e.to); };

        mgr.Move(1, 3); // pull B out past the run end

        // The manager re-gathered the run; the event reported only the op.
        Assert.Equal(new[] { a, c, b, x }, mgr.Tabs.ToArray());
        var move = Assert.Single(moves);
        Assert.Equal((b, 1, 3), move);

        // The replay followed the event's raw indices and stranded -- the seam.
        Assert.Equal(new[] { a, c, x, b }, strip.Items);

        Reconcile(mgr, strip);

        Assert.Equal(mgr.Tabs.ToArray(), strip.Items.ToArray());
    }

    // --- Seam b: a Move the invariants clamp to a no-op ---

    [Fact]
    public void A_clamped_move_mutates_nothing_raises_nothing_and_the_drop_reconciles_the_strip()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true); // prefix = [A]
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var strip = new Strip(mgr.Tabs);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        // TabView's reorder already put B in front of the pin; the manager
        // is asked to ratify a move into the pinned prefix.
        strip.Items.Remove(b);
        strip.Items.Insert(0, b);

        int mutations = ManagerMutations(mgr, () => CompleteDrag(mgr, strip, b));

        // The clamp reduced the move to a no-op: no list change, no event,
        // and the drop's reconcile pulled the strip back to the manager.
        Assert.Equal(0, mutations);
        Assert.Equal(0, moved);
        Assert.Equal(new[] { a, b, c }, mgr.Tabs.ToArray());
        Assert.Equal(mgr.Tabs.ToArray(), strip.Items.ToArray());
    }

    // --- The drop path: the audit invariant ---

    [Fact]
    public void A_drag_completion_leaves_manager_and_strip_in_agreement_and_moves_once()
    {
        var mgr = NewManager(2); // [A, B, C]
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var strip = new Strip(mgr.Tabs);
        // TabView applied the reorder its own way: C dragged one slot left.
        strip.Items.Remove(c);
        strip.Items.Insert(1, c);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        CompleteDrag(mgr, strip, c);

        Assert.Equal(1, moved);
        Assert.Equal(new[] { a, c, b }, mgr.Tabs.ToArray());
        Assert.Equal(mgr.Tabs.ToArray(), strip.Items.ToArray());
    }

    // --- Seam c: batched TabMoved pairs from run moves ---

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MoveRun_rotations_replayed_raw_converge_through_the_projector(int runSize)
    {
        for (int from = 0; from < runSize; from++)
        {
            for (int to = 0; to < runSize; to++)
            {
                if (from == to) continue;

                var mgr = NewManager(runSize - 1);
                var group = new TabGroup();
                mgr.GroupTabs(mgr.Tabs.ToArray(), group);
                var strip = new Strip(mgr.Tabs);
                mgr.TabMoved += (_, e) => strip.Replay(e.tab, e.to);

                mgr.MoveRun(from, to);

                AssertRunContiguous(mgr, group);
                // The rotation's paired events, replayed one by one, land
                // wherever they land; the reconcile guarantees the ending.
                Reconcile(mgr, strip);
                Assert.Equal(mgr.Tabs.ToArray(), strip.Items.ToArray());
            }
        }
    }

    [Fact]
    public void MoveGroup_across_a_nonmember_strands_a_raw_replay_and_the_projector_converges_it()
    {
        var mgr = NewManager(4); // [P, A, B, C, Q]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2], mgr.Tabs[3] }, group);
        var p = mgr.Tabs[0];
        var a = mgr.Tabs[1];
        var b = mgr.Tabs[2];
        var c = mgr.Tabs[3];
        var q = mgr.Tabs[4];
        var strip = new Strip(mgr.Tabs);
        mgr.TabMoved += (_, e) => strip.Replay(e.tab, e.to);

        mgr.MoveGroup(group, 4); // the run lands after Q

        Assert.Equal(new[] { p, q, a, b, c }, mgr.Tabs.ToArray());

        // Per-member pairs replayed one by one cannot express the block move.
        Assert.NotEqual(mgr.Tabs.ToArray(), strip.Items.ToArray());

        Reconcile(mgr, strip);

        Assert.Equal(mgr.Tabs.ToArray(), strip.Items.ToArray());
    }

    private static void AssertRunContiguous(TabManager mgr, TabGroup group)
    {
        var positions = new List<int>();
        for (int i = 0; i < mgr.Tabs.Count; i++)
            if (ReferenceEquals(mgr.Tabs[i].Group, group))
                positions.Add(i);
        Assert.NotEmpty(positions);
        Assert.Equal(positions[^1] - positions[0] + 1, positions.Count);
    }
}
