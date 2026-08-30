using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The engine's pin-boundary commit, executed against a real manager:
/// Classify first, SetPinned to relocate the row to the boundary, Move
/// to place it at the crossing's slot in the new zone, and the read-back
/// that catches a clamp. Manager state only -- the visual is the
/// engine's to rebind, and these are the states it must render.
/// The sequences here ARE the engine's CommitTabCrossing, minus the
/// host: if the manager sequence and the wiring ever drift apart, one
/// of the two is lying.
/// </summary>
public class TabEnginePinCrossingTests
{
    /// <summary>A manager holding exactly <paramref name="tabs"/> tabs:
    /// the constructor starts with one, so this adds the rest.</summary>
    private static TabManager NewManager(int tabs)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (var i = 1; i < tabs; i++) mgr.NewTab();
        return mgr;
    }

    /// <summary>The engine's commit sequence for one machine crossing,
    /// verbatim from CommitTabCrossing minus trace and visual.</summary>
    private static (bool Committed, int ActualSlot) CommitCrossing(
        TabManager mgr, TabModel dragged, int managerTo)
    {
        var from = mgr.IndexOf(dragged);
        var zone = TabPinBoundary.Classify(
            dragged.IsPinned, mgr.PinCount, mgr.Tabs.Count, managerTo);
        if (zone.Op != TabPinZoneOp.None)
        {
            mgr.SetPinned(dragged, zone.Op == TabPinZoneOp.Pin);
            from = mgr.IndexOf(dragged);
            if (from < 0) return (false, -1);
        }
        mgr.Move(from, managerTo);

        var (_, managerIndex) = TabStripProjection.DragSlots(mgr);
        var actual = mgr.IndexOf(dragged);
        var actualSlot = managerIndex.IndexOf(actual);
        if (actual != managerTo || actualSlot < 0) return (false, actualSlot);
        return (true, actualSlot);
    }

    [Fact]
    public void An_unpinned_row_crossing_into_the_prefix_pins_and_lands()
    {
        var mgr = NewManager(3);
        var head = mgr.Tabs[0];
        var dragged = mgr.Tabs[2];
        mgr.SetPinned(head, true);
        // [pinned, A, B]; B crossing left past the boundary targets slot
        // 0 -- on the pinned side of PinCount=1, so classify calls it a
        // pin, SetPinned relocates B to the boundary, and the Move lands
        // it at the crossing's slot.
        var zone = TabPinBoundary.Classify(
            dragged.IsPinned, mgr.PinCount, mgr.Tabs.Count, 0);
        Assert.Equal(TabPinZoneOp.Pin, zone.Op);

        var (committed, slot) = CommitCrossing(mgr, dragged, 0);

        Assert.True(committed);
        Assert.Equal(0, slot);
        Assert.True(dragged.IsPinned);
        Assert.Equal(2, mgr.PinCount);
        Assert.Equal(new[] { dragged, head, mgr.Tabs[2] }, mgr.Tabs.ToArray());
    }

    [Fact]
    public void A_pinned_row_crossing_out_of_the_prefix_unpins_and_lands()
    {
        var mgr = NewManager(3);
        var dragged = mgr.Tabs[0];
        mgr.SetPinned(dragged, true);
        mgr.SetPinned(mgr.Tabs[1], true);
        // [p0, p1, B]; p0 crossing right past the boundary targets slot 2,
        // beyond PinCount=2 -- an unpin, the relocation to the body's
        // first slot, and the Move carrying it the rest of the way.
        var zone = TabPinBoundary.Classify(
            dragged.IsPinned, mgr.PinCount, mgr.Tabs.Count, 2);
        Assert.Equal(TabPinZoneOp.Unpin, zone.Op);

        var (committed, slot) = CommitCrossing(mgr, dragged, 2);

        Assert.True(committed);
        Assert.Equal(2, slot);
        Assert.False(dragged.IsPinned);
        Assert.Equal(1, mgr.PinCount);
        Assert.Equal(new[] { mgr.Tabs[0], mgr.Tabs[1], dragged }, mgr.Tabs.ToArray());
    }

    [Fact]
    public void A_grouped_row_crossing_in_leaves_its_run_and_pins()
    {
        var mgr = NewManager(4);
        var head = mgr.Tabs[0];
        var dragged = mgr.Tabs[2];
        mgr.SetPinned(head, true);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[2], mgr.Tabs[3] }, group);
        // [pinned, A, B, C] with {B, C} grouped; B crossing into the
        // prefix targets slot 0 -- the pinned side of PinCount=1 -- and
        // the manager's own SetPinned contract ungroups it first: the
        // commit lands it pinned and alone, its run left behind.
        var zone = TabPinBoundary.Classify(
            dragged.IsPinned, mgr.PinCount, mgr.Tabs.Count, 0);
        Assert.Equal(TabPinZoneOp.Pin, zone.Op);

        var (committed, _) = CommitCrossing(mgr, dragged, 0);

        Assert.True(committed);
        Assert.True(dragged.IsPinned);
        Assert.Null(dragged.Group);
        Assert.Equal(2, mgr.PinCount);
        Assert.Equal(0, mgr.IndexOf(dragged));
    }

    [Fact]
    public void A_same_zone_crossing_commits_as_a_plain_move()
    {
        var mgr = NewManager(3);
        var head = mgr.Tabs[0];
        var dragged = mgr.Tabs[1];
        var other = mgr.Tabs[2];
        mgr.SetPinned(head, true);
        // [pinned, A, B]; A crossing right to the last slot stays inside
        // the body zone -- a plain Move, no pin bit touched.
        var zone = TabPinBoundary.Classify(
            dragged.IsPinned, mgr.PinCount, mgr.Tabs.Count, 2);
        Assert.Equal(TabPinZoneOp.None, zone.Op);

        var (committed, slot) = CommitCrossing(mgr, dragged, 2);

        Assert.True(committed);
        Assert.Equal(2, slot);
        Assert.False(dragged.IsPinned);
        Assert.Equal(1, mgr.PinCount);
        Assert.Equal(new[] { head, other, dragged }, mgr.Tabs.ToArray());
    }

    [Fact]
    public void The_unit_space_never_offers_a_crossing_the_clamp_would_refuse()
    {
        // The chip drag's unit space is body-only: whatever the machine
        // offers, MoveGroup's clamp window covers it, so a committed
        // crossing lands. One crossing, executed: the lone tab crosses
        // the chip'd run downward and the read-back agrees with the
        // formula.
        var mgr = NewManager(3);
        var runHead = mgr.Tabs[0];
        var runMate = mgr.Tabs[1];
        var lone = mgr.Tabs[2];
        var group = new TabGroup();
        mgr.GroupTabs(new[] { runHead, runMate }, group);
        mgr.Activate(runMate);

        var units = TabGroupDragUnits.Build(mgr);
        var dragged = units.Count - 1;
        var pivot = 0;
        var target = TabGroupDragUnits.TargetAfter(units, units[dragged], pivot);
        mgr.MoveGroup(group, target);

        var nowUnits = TabGroupDragUnits.Build(mgr);
        var now = -1;
        for (var i = 0; i < nowUnits.Count; i++)
        {
            if (ReferenceEquals(nowUnits[i].Group, group)) { now = i; break; }
        }
        Assert.True(now >= 0 && nowUnits[now].First == target,
            "the read-back must agree with the formula on a body-space "
            + "crossing: a mismatch here is the clamp the unit space promised "
            + "never to provoke.");
        Assert.Equal(new[] { lone, runHead, runMate }, mgr.Tabs.ToArray());
    }
}
