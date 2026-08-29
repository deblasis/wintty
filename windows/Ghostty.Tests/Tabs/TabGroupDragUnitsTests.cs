using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The unit space a vertical group drag speaks, and the two MoveGroup
/// targets its crossings map to. This is strip-private mapping of
/// exactly the kind 5b-1's lesson is about: a flipped direction here
/// passes every source scan and every wiring pin, so the target
/// formulas are pinned by EXECUTING them against a real manager and
/// asserting the final order -- the drag commits through MoveGroup, and
/// a wrong target is a wrong order, visibly.
/// </summary>
public class TabGroupDragUnitsTests
{
    private static TabManager NewManager(int count)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 0; i < count; i++) mgr.NewTab();
        return mgr;
    }

    // [t0, g1a, g1b, t3, g2a, g2b, g2c, t7] -- two runs of different
    // sizes around lone tabs, the shape that exposes an off-by-one or a
    // row-swap treated as a run-swap. The manager seeds one tab, so
    // seven extra makes the eight the indices below speak of.
    private static (TabManager Mgr, TabModel[] T, TabGroup G1, TabGroup G2) NewMixed()
    {
        var mgr = NewManager(7);
        var t = mgr.Tabs.ToArray();
        var g1 = mgr.CreateGroup(t[1])!;
        mgr.JoinGroup(t[2], g1);
        var g2 = mgr.CreateGroup(t[4])!;
        mgr.JoinGroup(t[5], g2);
        mgr.JoinGroup(t[6], g2);
        return (mgr, t, g1, g2);
    }

    [Fact]
    public void Build_names_one_unit_per_run_in_manager_order()
    {
        var (mgr, t, g1, g2) = NewMixed();

        var units = TabGroupDragUnits.Build(mgr);

        Assert.Equal(5, units.Count);
        Assert.Null(units[0].Group);
        Assert.Equal(0, units[0].First);
        Assert.Equal(1, units[0].Count);
        Assert.Same(g1, units[1].Group);
        Assert.Equal(1, units[1].First);
        Assert.Equal(2, units[1].Count);
        Assert.Same(t[1], units[1].Rep);
        Assert.Null(units[2].Group);
        Assert.Equal(3, units[2].First);
        Assert.Same(t[3], units[2].Rep);
        Assert.Same(g2, units[3].Group);
        Assert.Equal(4, units[3].First);
        Assert.Equal(3, units[3].Count);
        Assert.Null(units[4].Group);
        Assert.Equal(7, units[4].First);
    }

    [Fact]
    public void Build_skips_the_pinned_prefix_and_keeps_body_firsts()
    {
        var (mgr, t, _, _) = NewMixed();
        mgr.SetPinned(t[0], true);

        var units = TabGroupDragUnits.Build(mgr);

        // The prefix contributes no unit, and every body First counts
        // manager slots, pinned ones included: the mapping is into the
        // manager, not into the unit list.
        Assert.Equal(4, units.Count);
        Assert.Equal(new[] { 1, 3, 4, 7 }, units.Select(u => u.First).ToArray());
    }

    [Fact]
    public void Collapse_changes_nothing()
    {
        var (mgr, _, _, g2) = NewMixed();
        mgr.CollapseGroup(g2, true);

        // One unit per run whether every member is visible, only the
        // active one is, or none is: hidden members have no geometry,
        // but the run is still one atom the drag moves.
        var units = TabGroupDragUnits.Build(mgr);
        Assert.Equal(5, units.Count);

        // The collapsed run's own span is the formula input collapse
        // could plausibly bend -- counting visible members would read
        // 1 here and silently over-cross once the strip rides it.
        Assert.Equal(4, units[3].First);
        Assert.Equal(3, units[3].Count);
    }

    [Fact]
    public void TargetAfter_swaps_whole_runs_and_lands_whole()
    {
        var (mgr, t, g1, _) = NewMixed();
        var units = TabGroupDragUnits.Build(mgr);

        // g1 down past the lone tab at 3: head lands at 3 + 1 - 2.
        var target = TabGroupDragUnits.TargetAfter(units, units[1], pivot: 2);
        Assert.Equal(2, target);
        mgr.MoveGroup(g1, target);

        Assert.Equal(new[] { t[0], t[3], t[1], t[2], t[4], t[5], t[6], t[7] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void TargetAfter_crosses_an_entire_neighbour_run()
    {
        var (mgr, t, g1, g2) = NewMixed();
        var units = TabGroupDragUnits.Build(mgr);

        // g1 down past g2 (three members): the head lands after the
        // whole neighbour run, never inside it.
        var target = TabGroupDragUnits.TargetAfter(units, units[1], pivot: 3);
        Assert.Equal(5, target);
        mgr.MoveGroup(g1, target);

        Assert.Equal(new[] { t[0], t[3], t[4], t[5], t[6], t[1], t[2], t[7] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void TargetBefore_lands_before_the_pivot_run()
    {
        var (mgr, t, _, g2) = NewMixed();
        var units = TabGroupDragUnits.Build(mgr);

        // g2 up past g1: its head lands at g1's first slot, the whole
        // run before the pivot run.
        var target = TabGroupDragUnits.TargetBefore(units, pivot: 1);
        Assert.Equal(1, target);
        mgr.MoveGroup(g2, target);

        Assert.Equal(new[] { t[0], t[4], t[5], t[6], t[1], t[2], t[3], t[7] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void TargetBefore_at_the_first_unit_lands_on_the_clamp_low_edge()
    {
        var (mgr, t, _, g2) = NewMixed();
        var units = TabGroupDragUnits.Build(mgr);

        // g2 up past unit 0: the pivot's First IS the clamp-low bound
        // (nothing pinned, so 0). The commit must ride the edge
        // exactly -- not a slot short into a refused crossing, not a
        // clamped near-miss.
        var target = TabGroupDragUnits.TargetBefore(units, pivot: 0);
        Assert.Equal(0, target);
        mgr.MoveGroup(g2, target);

        Assert.Equal(new[] { t[4], t[5], t[6], t[0], t[1], t[2], t[3], t[7] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void TargetAfter_at_the_last_unit_lands_on_the_clamp_top_edge()
    {
        var (mgr, t, g1, _) = NewMixed();
        var units = TabGroupDragUnits.Build(mgr);

        // g1 down past the last unit: 7 + 1 - 2 is exactly the clamp
        // top for a two-member run in eight tabs, so the run lands on
        // the final two slots whole and the formula needs no extra
        // clamping to be in range.
        var target = TabGroupDragUnits.TargetAfter(units, units[1], pivot: 4);
        Assert.Equal(6, target);
        mgr.MoveGroup(g1, target);

        Assert.Equal(new[] { t[0], t[3], t[4], t[5], t[6], t[7], t[1], t[2] },
            mgr.Tabs.ToArray());
    }

    [Fact]
    public void Down_then_up_round_trips()
    {
        var (mgr, _, g1, _) = NewMixed();
        var before = mgr.Tabs.ToArray();

        var units = TabGroupDragUnits.Build(mgr);
        mgr.MoveGroup(g1, TabGroupDragUnits.TargetAfter(units, units[1], pivot: 2));

        var after = TabGroupDragUnits.Build(mgr);
        int home = after.Select((u, i) => (u, i)).First(p => ReferenceEquals(p.u.Group, g1)).i;
        mgr.MoveGroup(g1, TabGroupDragUnits.TargetBefore(after, pivot: home - 1));

        Assert.Equal(before, mgr.Tabs.ToArray());
    }

    [Fact]
    public void A_single_unit_strip_has_nothing_to_reorder()
    {
        var mgr = NewManager(1);
        var g = mgr.CreateGroup(mgr.Tabs[0])!;
        mgr.JoinGroup(mgr.Tabs[1], g);

        // Two tabs, one run: the strip refuses to arm a drag, because
        // the machine needs two units to swap.
        Assert.Single(TabGroupDragUnits.Build(mgr));
    }
}
