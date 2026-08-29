using System;
using System.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The horizontal drop's slot-to-manager map, executed against a real
/// manager. This is the third strip-private mapping, and a transposed
/// boundary here passes every source scan and every wiring pin, so the
/// targets are pinned by asserting FINAL ORDER after the commit the
/// host really makes -- MoveGroup for a chip, Move for a positioning --
/// not by re-deriving the arithmetic.
/// </summary>
public class TabChipDropTests
{
    private static TabManager NewManager(int count)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 0; i < count; i++) mgr.NewTab();
        return mgr;
    }

    // [t0, g1a, g1b, t3, t4, t5] -- a collapsed run (the active tab stays
    // t0, so the run projects as a chip) flanked by lone tabs. Slots:
    // 0=t0, 1=Chip(G1), 2=t3, 3=t4, 4=t5.
    private static (TabManager Mgr, TabModel[] T, TabGroup G1) NewChipShape()
    {
        var mgr = NewManager(5);
        var t = mgr.Tabs.ToArray();
        var g1 = mgr.CreateGroup(t[1])!;
        mgr.JoinGroup(t[2], g1);
        mgr.CollapseGroup(g1, true);
        return (mgr, t, g1);
    }

    // [g1a, g1b, t2, g2c, g2d, t5] -- two collapsed runs around a lone
    // tab: the shape where a boundary read as "next slot" instead of
    // "end of the left run" swaps two chips instead of moving one.
    private static (TabManager Mgr, TabModel[] T, TabGroup G1, TabGroup G2) NewTwoChips()
    {
        var mgr = NewManager(5);
        var t = mgr.Tabs.ToArray();
        var g1 = mgr.CreateGroup(t[0])!;
        mgr.JoinGroup(t[1], g1);
        var g2 = mgr.CreateGroup(t[3])!;
        mgr.JoinGroup(t[4], g2);
        mgr.CollapseGroup(g1, true);
        mgr.CollapseGroup(g2, true);
        return (mgr, t, g1, g2);
    }

    [Fact]
    public void GroupTarget_swaps_the_run_whole_past_a_lone_tab()
    {
        var (mgr, t, g1) = NewChipShape();

        // Chip dragged rightward to rest between t4 and t5: the strip's
        // left neighbour is t4, and the run lands whole past t4, members
        // hidden.
        var target = TabChipDrop.GroupTarget(mgr, g1, leftTab: t[4], leftChip: null);
        mgr.MoveGroup(g1, target);

        Assert.Equal(new[] { t[0], t[3], t[4], t[1], t[2], t[5] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void GroupTarget_crosses_the_whole_left_run_when_the_left_neighbour_is_a_chip()
    {
        var (mgr, t, g1, g2) = NewTwoChips();

        // Chip2 dragged left to rest right after Chip1: the neighbour
        // stands for BOTH hidden members of G1, and the run must land
        // past the whole of it, not between the members. Coming from the
        // right, the departure adjustment must NOT apply -- the edge did
        // not move.
        var target = TabChipDrop.GroupTarget(mgr, g2, leftTab: null, leftChip: g1);
        mgr.MoveGroup(g2, target);

        Assert.Equal(new[] { t[0], t[1], t[3], t[4], t[2], t[5] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void GroupTarget_coming_from_the_left_subtracts_the_runs_own_departure()
    {
        // [g1a, g1b, t2, g2c, g2d, t5, t6] -- two collapsed runs, and a
        // KEPT tab after the rest point: at the strip's end MoveGroup's
        // clamp absorbs the subtraction, so the rest sits mid-strip
        // where the arithmetic alone decides the landing.
        var mgr = NewManager(6);
        var t = mgr.Tabs.ToArray();
        var g1 = mgr.CreateGroup(t[0])!;
        mgr.JoinGroup(t[1], g1);
        var g2 = mgr.CreateGroup(t[3])!;
        mgr.JoinGroup(t[4], g2);
        mgr.CollapseGroup(g1, true);
        mgr.CollapseGroup(g2, true);

        // Chip2 dragged right to rest between t5 and t6: its own two
        // members stop counting toward the landing -- without the
        // subtraction the run files in past t6 and splits the tail.
        var target = TabChipDrop.GroupTarget(mgr, g2, leftTab: t[5], leftChip: null);
        mgr.MoveGroup(g2, target);

        Assert.Equal(new[] { t[0], t[1], t[2], t[5], t[3], t[4], t[6] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void GroupTarget_at_the_strip_head_lands_on_the_prefix_edge_never_inside_it()
    {
        var (mgr, t, g1) = NewChipShape();
        mgr.SetPinned(t[0], true);

        // A rest at the head names no neighbour: the map answers 0 and
        // MoveGroup's clamp lifts the run to the prefix edge. Asserting
        // final order is the whole point -- an off-by-one here would file
        // the run INTO the pinned prefix, and the clamp is the backstop,
        // not the mapping.
        var target = TabChipDrop.GroupTarget(mgr, g1, leftTab: null, leftChip: null);
        mgr.MoveGroup(g1, target);

        Assert.Equal(new[] { t[0], t[1], t[2], t[3], t[4], t[5] },
            mgr.Tabs.ToArray());
        Assert.True(t[0].IsPinned);
        Assert.False(t[1].IsPinned);
    }

    [Fact]
    public void MemberTargetBefore_lands_before_the_whole_hidden_run()
    {
        var (mgr, t, g1) = NewChipShape();

        // t3 dragged to just before the chip: it must land before BOTH
        // hidden members, or it lands between the header and its run --
        // a split the projector cannot render.
        var target = TabChipDrop.MemberTargetBefore(mgr, g1);
        mgr.Move(mgr.IndexOf(t[3]), target);

        Assert.Equal(new[] { t[0], t[3], t[1], t[2], t[4], t[5] },
            mgr.Tabs.ToArray());
        Assert.Null(t[3].Group);
    }

    [Fact]
    public void MemberTargetAfter_lands_past_the_whole_hidden_run()
    {
        var (mgr, t, g1) = NewChipShape();

        // t4 dragged to just after the chip from two slots right of it:
        // the landing clears the hidden members and stays unjoined.
        var target = TabChipDrop.MemberTargetAfter(mgr, g1);
        mgr.Move(mgr.IndexOf(t[4]), target);

        Assert.Equal(new[] { t[0], t[1], t[2], t[4], t[3], t[5] },
            mgr.Tabs.ToArray());
        Assert.Null(t[4].Group);
    }

    // --- JoinGroup's collapse bit: the auto-expand is the manager's,
    // so neither strip can forget it or fire it on a refused join. ---

    [Fact]
    public void An_actual_join_expands_the_collapsed_run_so_the_join_is_visible()
    {
        var (mgr, t, g1) = NewChipShape();
        Assert.True(g1.IsCollapsed);

        mgr.JoinGroup(t[3], g1);

        Assert.Same(g1, t[3].Group);
        Assert.False(g1.IsCollapsed);
        // The joined member renders: an expanded run projects members,
        // not a chip, so the drop's result is on screen.
        var rows = TabStripProjection.HorizontalRows(mgr);
        Assert.DoesNotContain(rows, r => r is TabStripProjection.HorizontalRow.Chip);
        Assert.Contains(rows, r => r is TabStripProjection.HorizontalRow.Item
            { Tab: { } tab } && ReferenceEquals(tab, t[3]));
    }

    [Fact]
    public void An_already_member_join_leaves_the_collapse_bit_alone()
    {
        var (mgr, t, g1) = NewChipShape();

        mgr.JoinGroup(t[1], g1);

        Assert.True(g1.IsCollapsed);
        Assert.Equal(new[] { t[0], t[1], t[2], t[3], t[4], t[5] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void A_refused_join_leaves_the_collapse_bit_alone()
    {
        var (mgr, t, g1) = NewChipShape();
        mgr.SetPinned(t[3], true);
        // SetPinned relocates to the prefix boundary, which is slot 0
        // here: that state is the baseline the refused join must leave
        // exactly where it is.
        var pinned = new[] { t[3], t[0], t[1], t[2], t[4], t[5] };
        Assert.Equal(pinned, mgr.Tabs.ToArray());

        // A pinned tab cannot join (the prefix outranks membership); the
        // expansion rides the join that did not happen, so the bit and
        // the run both stay as the user left them.
        mgr.JoinGroup(t[3], g1);

        Assert.True(g1.IsCollapsed);
        Assert.Null(t[3].Group);
        Assert.Equal(pinned, mgr.Tabs.ToArray());
    }
}
