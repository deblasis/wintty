using System;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The pinned prefix is a slot range to the drag machine, so the only new
/// decision a pinned-zone drag makes is what one crossing MEANS: stay in
/// the zone, or carry the row across the boundary. That decision is pure
/// arithmetic over (pinned, PinCount, rowCount, slot), so it lives in Core
/// next to the machine and the whole boundary grammar is pinned here.
/// The strip's job afterwards is mechanical: SetPinned then Move, read
/// the truth back.
/// </summary>
public class TabPinBoundaryTests
{
    [Fact]
    public void A_crossing_inside_one_zone_is_not_a_zone_change()
    {
        // [P, P, U, U]: pinned moves within the prefix...
        Assert.Equal(TabPinZoneOp.None,
            TabPinBoundary.Classify(draggedIsPinned: true, pinCount: 2, rowCount: 4, to: 1).Op);
        // ...and unpinned moves within the rest.
        Assert.Equal(TabPinZoneOp.None,
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: 2, rowCount: 4, to: 3).Op);
    }

    [Fact]
    public void An_unpinned_row_crossing_up_pins()
    {
        var crossing = TabPinBoundary.Classify(
            draggedIsPinned: false, pinCount: 2, rowCount: 4, to: 1);
        Assert.Equal(TabPinZoneOp.Pin, crossing.Op);
        Assert.Equal(1, crossing.To);
    }

    [Fact]
    public void A_pinned_row_crossing_down_unpins()
    {
        var crossing = TabPinBoundary.Classify(
            draggedIsPinned: true, pinCount: 2, rowCount: 4, to: 2);
        Assert.Equal(TabPinZoneOp.Unpin, crossing.Op);
        Assert.Equal(2, crossing.To);
    }

    [Fact]
    public void The_first_unpinned_slot_is_outside_the_zone()
    {
        // to == PinCount is the first unpinned slot: not a pin. This is
        // the off-by-one that would pin on a harmless nudge if flipped.
        Assert.Equal(TabPinZoneOp.None,
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: 2, rowCount: 4, to: 2).Op);
    }

    [Fact]
    public void The_last_pinned_slot_is_inside_the_zone()
    {
        Assert.Equal(TabPinZoneOp.Pin,
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: 2, rowCount: 4, to: 1).Op);
    }

    [Fact]
    public void With_no_pins_nothing_can_cross()
    {
        Assert.Equal(TabPinZoneOp.None,
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: 0, rowCount: 3, to: 0).Op);
    }

    [Fact]
    public void With_all_rows_pinned_there_is_no_way_out()
    {
        // Every slot is inside the prefix, so no crossing an all-pinned
        // window can produce reads as an unpin -- the row has nowhere to
        // go, which is the manager's own clamp expressed as a class.
        for (int to = 0; to < 3; to++)
            Assert.Equal(TabPinZoneOp.None,
                TabPinBoundary.Classify(draggedIsPinned: true, pinCount: 3, rowCount: 3, to).Op);
    }

    [Fact]
    public void An_out_of_range_slot_is_never_promoted_to_a_zone_change()
    {
        // The machine never emits one, and a malformed slot must not
        // become a pin toggle: None sends it to Move, which refuses, and
        // the strip's read-back catches the refusal.
        Assert.Equal(TabPinZoneOp.None,
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: 2, rowCount: 4, to: -1).Op);
        Assert.Equal(TabPinZoneOp.None,
            TabPinBoundary.Classify(draggedIsPinned: true, pinCount: 2, rowCount: 4, to: 4).Op);
    }

    [Fact]
    public void PinCount_outside_the_row_span_is_corrupt_state()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: -1, rowCount: 4, to: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TabPinBoundary.Classify(draggedIsPinned: false, pinCount: 5, rowCount: 4, to: 0));
    }
}
