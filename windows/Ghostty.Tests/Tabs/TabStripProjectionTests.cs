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
/// the shell's Remove-then-Insert discipline on the other.
///
/// The three seams here are the ones PR 2 owns (spec 10, PR 2 row):
/// raw TabMoved indices that Normalize has since repaired, a Move the
/// invariants clamp down to a silent no-op, and the batched TabMoved
/// pairs of a run move. The shell cannot load into this host, so the
/// mirror stands in for TabItems; TabStripSyncWiringTests pins the real
/// handlers to the same replay-then-reconcile shape.
/// </summary>
public class TabStripProjectionTests
{
    private static TabManager NewManager(int extraTabs)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 0; i < extraTabs; i++) mgr.NewTab();
        return mgr;
    }

    /// <summary>
    /// A live strip: the manager's tabs in some order, mutated only by
    /// the shell's remove-then-insert discipline. Stands in for TabItems
    /// / MenuItems, which this test host cannot construct.
    /// </summary>
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
    /// OnTabDragCompleted's shape, driven against a live mirror. The
    /// wiring test pins the real handler to the same three steps: read
    /// the strip index, Move in manager space, reconcile the strip to
    /// the manager's final order.
    /// </summary>
    private static void CompleteDrag(TabManager mgr, Strip strip, TabModel dragged)
    {
        int newIndex = strip.Items.IndexOf(dragged);
        int oldIndex = mgr.IndexOf(dragged);
        if (oldIndex != newIndex && oldIndex >= 0)
            mgr.Move(oldIndex, newIndex);
        strip.Apply(TabStripProjection.Diff(
            TabStripProjection.Rows(mgr), strip.Items));
    }

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

        // The manager re-gathered the run; the event reported only the raw op.
        Assert.Equal(new[] { a, c, b, x }, mgr.Tabs.ToArray());
        var move = Assert.Single(moves);
        Assert.Equal((b, 1, 3), move);

        // The replay followed the event's indices and stranded desynced --
        // the seam, observed before the repair.
        Assert.Equal(new[] { a, c, x, b }, strip.Items);

        strip.Apply(TabStripProjection.Diff(
            TabStripProjection.Rows(mgr), strip.Items));

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

        // TabView's own reorder has already put B in front of the pin; the
        // manager is asked to ratify a move into the pinned prefix.
        strip.Items.Remove(b);
        strip.Items.Insert(0, b);

        int mutations = ManagerMutations(mgr, () => CompleteDrag(mgr, strip, b));

        // The clamp reduced the move to a no-op: no list change, no event.
        Assert.Equal(0, mutations);
        Assert.Equal(0, moved);
        Assert.Equal(new[] { a, b, c }, mgr.Tabs.ToArray());

        // The drop's reconcile pulled the strip back to the manager's order.
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
                // wherever they land; the reconcile is what guarantees the
                // strip ends on the manager's order.
                strip.Apply(TabStripProjection.Diff(
                    TabStripProjection.Rows(mgr), strip.Items));
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

        // The per-member pairs replayed one by one cannot express the
        // block move -- the seam, observed before the repair.
        Assert.NotEqual(mgr.Tabs.ToArray(), strip.Items.ToArray());

        strip.Apply(TabStripProjection.Diff(
            TabStripProjection.Rows(mgr), strip.Items));

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
