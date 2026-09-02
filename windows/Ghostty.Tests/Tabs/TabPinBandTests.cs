using System;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The pinned zone is a band of icon squares that wraps, and that shape is
/// what separates the zones now that no rule is drawn between them. Three
/// readers depend on the same arithmetic -- the panel that arranges the
/// band, the drop preview that promises the next slot, and the geometry
/// harness that measures the result -- so the arithmetic is pinned here,
/// where it needs no WinUI host to answer.
///
/// The numbers this fixes: the 48px compact rail carries exactly one
/// column, the 220px expanded pane carries four, and a band never reports
/// zero columns however narrow the pane gets.
/// </summary>
public class TabPinBandTests
{
    /// <summary>
    /// The band's own inset in the strip: the rows' 4px left inset, and
    /// the same again at the right so a column is not flush to the pane
    /// edge. What the strip actually hands the panel is the pane width
    /// less this.
    /// </summary>
    private const double PaneInset = 8;

    [Fact]
    public void The_compact_rail_carries_exactly_one_column()
    {
        // 48px pane, less the band's insets, is one square and no gutter.
        Assert.Equal(1, TabPinBand.ColumnsFor(48 - PaneInset));
    }

    [Fact]
    public void The_expanded_pane_carries_four_columns()
    {
        // 220px pane: four 40px squares and three 4px gutters is 172,
        // and a fifth would need another 44.
        Assert.Equal(4, TabPinBand.ColumnsFor(220 - PaneInset));
    }

    [Fact]
    public void Three_pins_cost_one_band_row_in_the_expanded_pane()
    {
        var columns = TabPinBand.ColumnsFor(220 - PaneInset);
        Assert.Equal(1, TabPinBand.RowsFor(3, columns));
        // The whole point of the shape: as rows they cost three.
        Assert.Equal(3, TabPinBand.RowsFor(3, TabPinBand.ColumnsFor(48 - PaneInset)));
    }

    [Fact]
    public void A_band_narrower_than_one_square_still_has_a_column()
    {
        // A zero-column band divides by zero in every slot query and
        // reports no height for pins that exist. One is the floor.
        Assert.Equal(1, TabPinBand.ColumnsFor(0));
        Assert.Equal(1, TabPinBand.ColumnsFor(12));
        Assert.Equal(1, TabPinBand.ColumnsFor(double.NaN));
    }

    [Fact]
    public void Slots_run_in_reading_order()
    {
        // Left to right, then down: the same order the pinned prefix is
        // in, so the band never reorders what the manager holds.
        Assert.Equal((0, 0), TabPinBand.SlotOf(0, columns: 3));
        Assert.Equal((0, 2), TabPinBand.SlotOf(2, columns: 3));
        Assert.Equal((1, 0), TabPinBand.SlotOf(3, columns: 3));
        Assert.Equal((2, 1), TabPinBand.SlotOf(7, columns: 3));
    }

    [Fact]
    public void A_slot_origin_is_the_pitch_times_the_slot()
    {
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        Assert.Equal((0d, 0d), TabPinBand.OriginOf(0, columns: 3));
        Assert.Equal((2 * pitch, 0d), TabPinBand.OriginOf(2, columns: 3));
        Assert.Equal((0d, pitch), TabPinBand.OriginOf(3, columns: 3));
    }

    [Fact]
    public void The_slot_one_past_the_end_is_where_the_next_pin_lands()
    {
        // What the drop preview promises. On a full row it wraps, which
        // is exactly the case arithmetic derived from "one row pitch
        // below the last square" would get wrong.
        var (x, y) = TabPinBand.OriginOf(3, columns: 3);
        Assert.Equal(0d, x);
        Assert.Equal(TabPinBand.ChipSize + TabPinBand.ChipGap, y);
    }

    [Fact]
    public void An_empty_band_measures_nothing()
    {
        Assert.Equal(0, TabPinBand.RowsFor(0, columns: 4));
        Assert.Equal(0d, TabPinBand.BandWidth(0, columns: 4));
        Assert.Equal(0d, TabPinBand.BandHeight(0, columns: 4));
    }

    [Fact]
    public void The_band_measures_the_columns_in_use_and_the_rows_it_fills()
    {
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        // Two squares in a four-column band: two wide, one tall.
        Assert.Equal(2 * TabPinBand.ChipSize + TabPinBand.ChipGap,
            TabPinBand.BandWidth(2, columns: 4));
        Assert.Equal(TabPinBand.ChipSize, TabPinBand.BandHeight(2, columns: 4));
        // Five squares: the band is full width and two rows tall.
        Assert.Equal(4 * TabPinBand.ChipSize + 3 * TabPinBand.ChipGap,
            TabPinBand.BandWidth(5, columns: 4));
        Assert.Equal(pitch + TabPinBand.ChipSize, TabPinBand.BandHeight(5, columns: 4));
    }

    [Fact]
    public void One_column_is_pitch_identical_to_the_rows_it_replaced()
    {
        // The compact rail's band and the stack of 40px rows it replaced
        // put their squares on the same vertical pitch, so a pin landing
        // in a compact rail does not shift the list under it.
        Assert.Equal(44d, TabPinBand.ChipSize + TabPinBand.ChipGap);
        Assert.Equal(3 * 44d - TabPinBand.ChipGap, TabPinBand.BandHeight(3, columns: 1));
    }

    [Fact]
    public void A_column_appears_exactly_when_its_pitch_is_paid_for()
    {
        // The boundaries themselves, because the "+ ChipGap before the
        // divide" is what makes the last column not owe a trailing gutter,
        // and an off-by-one there is invisible everywhere except here.
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        Assert.Equal(1, TabPinBand.ColumnsFor(TabPinBand.ChipSize));
        Assert.Equal(1, TabPinBand.ColumnsFor(TabPinBand.ChipSize - 0.01));
        // Two squares and one gutter is exactly two columns; a hair under
        // is still one.
        Assert.Equal(2, TabPinBand.ColumnsFor(2 * TabPinBand.ChipSize + TabPinBand.ChipGap));
        Assert.Equal(1, TabPinBand.ColumnsFor(2 * TabPinBand.ChipSize + TabPinBand.ChipGap - 0.01));
        Assert.Equal(3, TabPinBand.ColumnsFor(3 * pitch - TabPinBand.ChipGap));
    }

    [Fact]
    public void An_unbounded_width_is_one_row_rather_than_a_wrapped_negative()
    {
        // A parent that measures with infinity is ordinary WinUI. The
        // arithmetic does not degrade to it on its own: (int) of an
        // infinite double is undefined in C# and lands on int.MinValue,
        // which the column floor would turn into a ONE-column band -- every
        // pin stacked vertically in a pane with room for a dozen.
        var columns = TabPinBand.ColumnsFor(double.PositiveInfinity);
        Assert.True(columns > 1000, "an unbounded width is not one column");

        // ...and the row count must survive that number. The ceiling
        // division adds `columns` to the count, which overflows unchecked
        // and wraps negative: the band reported zero rows for squares that
        // exist, so nothing was given any height to arrange in.
        Assert.Equal(1, TabPinBand.RowsFor(3, columns));
        Assert.Equal(1, TabPinBand.RowsFor(1, columns));
        Assert.Equal(TabPinBand.ChipSize, TabPinBand.BandHeight(3, columns));
    }

    [Fact]
    public void The_bands_own_width_is_not_where_its_column_count_comes_from()
    {
        // The trap the panel walks into if it re-derives columns from the
        // size it was ARRANGED at. The band is left-aligned, so that size
        // is its own desired width -- and feeding that back through
        // ColumnsFor answers "as many columns as there are squares".
        const double paneWidth = 220 - PaneInset;
        var capacity = TabPinBand.ColumnsFor(paneWidth);
        Assert.Equal(4, capacity);

        var width = TabPinBand.BandWidth(3, capacity);
        Assert.Equal(3, TabPinBand.ColumnsFor(width));

        // Which matters for exactly one thing: the slot one past the end,
        // which is the only slot the drop preview draws. Under the wrong
        // count it promises a second band row; under the right one it is
        // the fourth column of the first.
        Assert.Equal((1, 0), TabPinBand.SlotOf(3, TabPinBand.ColumnsFor(width)));
        Assert.Equal((0, 3), TabPinBand.SlotOf(3, capacity));
    }

    [Fact]
    public void The_nearest_slot_is_the_square_the_pointer_is_over()
    {
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        // Dead centre of each of four squares on one row.
        for (int i = 0; i < 4; i++)
        {
            var x = i * pitch + TabPinBand.ChipSize / 2;
            Assert.Equal(i, TabPinBand.NearestSlot(x, TabPinBand.ChipSize / 2, 4, 4));
        }
        // ...and the second row, which is the half a crossing engine on one
        // axis cannot reach at all.
        Assert.Equal(4, TabPinBand.NearestSlot(
            TabPinBand.ChipSize / 2, pitch + TabPinBand.ChipSize / 2, 4, 8));
        Assert.Equal(6, TabPinBand.NearestSlot(
            2 * pitch + TabPinBand.ChipSize / 2, pitch + TabPinBand.ChipSize / 2, 4, 8));
    }

    [Fact]
    public void The_answer_changes_at_the_midpoint_between_two_squares()
    {
        // Half a pitch of hysteresis, and it costs no state. The crossing
        // engine spends 8px and has to remember which way it last went.
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        var midpoint = TabPinBand.ChipSize / 2 + pitch / 2;
        var y = TabPinBand.ChipSize / 2;
        Assert.Equal(0, TabPinBand.NearestSlot(midpoint - 0.5, y, 4, 4));
        Assert.Equal(1, TabPinBand.NearestSlot(midpoint + 0.5, y, 4, 4));
        // A tie goes to the lower slot, so one gesture cannot land two ways
        // depending on which side the pointer arrived from.
        Assert.Equal(0, TabPinBand.NearestSlot(midpoint, y, 4, 4));
    }

    [Fact]
    public void A_pointer_past_the_end_of_a_row_belongs_to_the_last_square_on_it()
    {
        // Nearest centre, not containment: the gutters and the ragged end
        // of a partial row have to belong to something, or a drop there
        // reads as "nowhere" and falls through to the list below.
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        var y = TabPinBand.ChipSize / 2;
        // Three pins in a four-column band: well past the third square.
        Assert.Equal(2, TabPinBand.NearestSlot(3 * pitch + 100, y, 4, 3));
        // ...and before the first.
        Assert.Equal(0, TabPinBand.NearestSlot(-100, y, 4, 3));
        // Below the last row, which is where a pointer on its way out of
        // the band sits for a frame.
        Assert.Equal(2, TabPinBand.NearestSlot(2 * pitch + 20, 500, 4, 3));
    }

    [Fact]
    public void The_slot_one_past_the_end_is_reachable_only_when_asked_for()
    {
        // The difference between a reorder and a drop from outside. Three
        // pins in a four-column band, pointer over the empty fourth column:
        // a reorder has nowhere to put it but the third square, and an
        // insertion has the slot the drop preview is drawing.
        const double pitch = TabPinBand.ChipSize + TabPinBand.ChipGap;
        var x = 3 * pitch + TabPinBand.ChipSize / 2;
        var y = TabPinBand.ChipSize / 2;
        Assert.Equal(2, TabPinBand.NearestSlot(x, y, 4, slotCount: 3));
        Assert.Equal(3, TabPinBand.NearestSlot(x, y, 4, slotCount: 4));
    }

    [Fact]
    public void A_slotless_band_is_corrupt_state_not_a_hit_test()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabPinBand.NearestSlot(0, 0, columns: 4, slotCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabPinBand.NearestSlot(0, 0, columns: 0, slotCount: 3));
    }

    [Fact]
    public void A_bandless_column_count_is_corrupt_state_not_a_layout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabPinBand.RowsFor(3, columns: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabPinBand.SlotOf(0, columns: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabPinBand.BandWidth(3, columns: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabPinBand.SlotOf(-1, columns: 3));
    }
}
