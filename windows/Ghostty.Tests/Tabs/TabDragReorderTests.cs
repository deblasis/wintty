using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The drag state machine behind the vertical strip's drag-to-reorder:
/// press threshold, commit-on-center-crossing with hysteresis,
/// autoscroll ramp, release velocity, and the terminal transitions.
/// Pure class, so the gesture grammar is pinned without a WinUI host;
/// the composition wiring it feeds is in VerticalTabStrip and the
/// SendInput-level harness is the noted follow-up.
/// </summary>
public class TabDragReorderTests
{
    // Four rows of 40px: arranged centers 20, 60, 100, 140.
    private static TabDragReorder NewMachine(int grabIndex = 0, int rowCount = 4)
    {
        var machine = new TabDragReorder(rowCount, grabIndex);
        machine.UpdateCenters(new double[] { 20, 60, 100, 140 });
        return machine;
    }

    [Fact]
    public void Under_threshold_travel_stays_a_click()
    {
        var machine = NewMachine();
        machine.Press(20);

        Assert.False(machine.Begin(23));
        Assert.Equal(TabDragPhase.Pressed, machine.Phase);
    }

    [Fact]
    public void Threshold_travel_lifts_to_dragging()
    {
        var machine = NewMachine();
        machine.Press(20);

        Assert.True(machine.Begin(25));
        Assert.Equal(TabDragPhase.Dragging, machine.Phase);
    }

    [Fact]
    public void Crossing_commits_only_past_the_hysteresis()
    {
        var machine = NewMachine(grabIndex: 0);
        machine.Press(20);
        machine.Begin(40);

        // Neighbour center 60, hysteresis 8: the crossing fires past 68.
        Assert.Null(machine.Evaluate(68));
        var crossing = machine.Evaluate(69);
        Assert.Equal(new TabDragCrossing(0, 1), crossing);
        Assert.Equal(1, machine.Index);
    }

    [Fact]
    public void A_wobble_back_under_the_threshold_does_not_oscillate()
    {
        var machine = NewMachine(grabIndex: 0);
        machine.Press(20);
        machine.Begin(40);
        Assert.NotNull(machine.Evaluate(69));

        // After the commit the neighbour owns slot 0 (center 20), so the
        // order only flips back past 20 minus the hysteresis. The wobble
        // band between the commit point (68) and the flip-back point (12)
        // is nearly a full row wide: a shaking hand at 55 cannot jitter
        // the order.
        Assert.Null(machine.Evaluate(69));
        Assert.Null(machine.Evaluate(55));
        Assert.Null(machine.Evaluate(19));
        Assert.Equal(new TabDragCrossing(1, 0), machine.Evaluate(11));
        Assert.Equal(0, machine.Index);
        // And the round trip is stable: parked on the boundary again,
        // nothing re-fires without real travel.
        Assert.Null(machine.Evaluate(11));
    }

    [Fact]
    public void A_fast_flick_chains_one_crossing_per_evaluate()
    {
        var machine = NewMachine(grabIndex: 0);
        machine.Press(20);
        machine.Begin(30);

        // Straight to the bottom row: each call walks one slot.
        Assert.Equal(new TabDragCrossing(0, 1), machine.Evaluate(120));
        Assert.Equal(new TabDragCrossing(1, 2), machine.Evaluate(120));
        Assert.Null(machine.Evaluate(120));
        Assert.Equal(2, machine.Index);
    }

    [Fact]
    public void Chained_crossings_keep_the_slot_centers_true()
    {
        // Centers are indexed by manager order, and rows occupy slots in
        // manager order once layout catches up -- so two committed
        // crossings leave every center exactly where the strip is
        // arranging toward, and only the dragged index moves.
        var machine = NewMachine(grabIndex: 0);
        machine.Press(20);
        machine.Begin(30);
        Assert.Equal(new TabDragCrossing(0, 1), machine.Evaluate(110));
        Assert.Equal(new TabDragCrossing(1, 2), machine.Evaluate(110));

        Assert.Equal(2, machine.Index);
        Assert.Equal(20, machine.CenterOf(0));
        Assert.Equal(60, machine.CenterOf(1));
        Assert.Equal(100, machine.CenterOf(2));
        // The next neighbour up still gates on its true slot: flipping
        // back past row 1 needs the pointer under 60 minus 8.
        Assert.Null(machine.Evaluate(55));
        Assert.Equal(new TabDragCrossing(2, 1), machine.Evaluate(51));
    }

    [Fact]
    public void Upward_crossings_are_symmetric()
    {
        var machine = NewMachine(grabIndex: 3);
        machine.Press(140);
        machine.Begin(120);

        Assert.Null(machine.Evaluate(92));
        Assert.Equal(new TabDragCrossing(3, 2), machine.Evaluate(91));
        Assert.Equal(2, machine.Index);
    }

    [Fact]
    public void Remeasured_centers_replace_the_synthetic_ones()
    {
        var machine = NewMachine(grabIndex: 1);
        machine.Press(60);
        machine.Begin(70);
        machine.UpdateCenters(new double[] { 30, 70, 110, 150 });

        Assert.Equal(70, machine.CenterOf(1));
        // Past neighbour two's center 110 + 8: commits one slot.
        Assert.Equal(new TabDragCrossing(1, 2), machine.Evaluate(119));
    }

    [Fact]
    public void Membership_change_moves_the_dragged_index_with_it()
    {
        var machine = NewMachine(grabIndex: 2);
        machine.Press(100);
        machine.Begin(110);
        machine.UpdateIndex(3);

        Assert.Equal(3, machine.Index);
        // Center for slot 3 is 140; crossing up against slot 2 at 100 - 8.
        Assert.Equal(new TabDragCrossing(3, 2), machine.Evaluate(91));
    }

    [Fact]
    public void Autoscroll_ramps_with_edge_distance_and_signs_by_edge()
    {
        var machine = NewMachine();
        const double top = 100, bottom = 500;

        Assert.Equal(0, machine.AutoscrollSpeed(300, top, bottom));
        Assert.Equal(-TabStripMotion.AutoscrollBasePxPerSecond,
            machine.AutoscrollSpeed(124, top, bottom));
        Assert.Equal(-TabStripMotion.AutoscrollMaxPxPerSecond,
            machine.AutoscrollSpeed(95, top, bottom));
        Assert.Equal(TabStripMotion.AutoscrollMaxPxPerSecond,
            machine.AutoscrollSpeed(bottom - 4, top, bottom));

        // Midway down the band the ramp is halfway between the two tiers.
        var mid = machine.AutoscrollSpeed(top + 16, top, bottom);
        Assert.Equal(
            -(TabStripMotion.AutoscrollBasePxPerSecond
              + TabStripMotion.AutoscrollMaxPxPerSecond) / 2,
            mid, 1);
    }

    [Fact]
    public void Release_velocity_reads_the_trailing_window_only()
    {
        var machine = NewMachine();
        machine.Press(0);
        machine.Begin(10);
        machine.SampleVelocity(0, 0);
        machine.SampleVelocity(1000, 50);
        machine.SampleVelocity(50, 400); // the two samples above expire here
        machine.SampleVelocity(55, 450);

        // Only the trailing pair survives the window: 50 -> 55 over 50ms
        // is 100 px/s downward, not the 20,000 px/s the expired pair saw.
        Assert.InRange(machine.ReleaseVelocity(1000), 80, 120);
    }

    [Fact]
    public void Release_velocity_is_clamped_to_the_remaining_travel_per_period()
    {
        var machine = NewMachine();
        machine.Press(0);
        machine.Begin(10);
        machine.SampleVelocity(0, 0);
        machine.SampleVelocity(5000, 10);

        // 50ms period, 60px to travel: a fling is capped at 1200 px/s.
        Assert.Equal(1200, machine.ReleaseVelocity(60), 1);
        // A release moving away from the slot carries no settle velocity:
        // the spring owns the direction.
        Assert.Equal(0, machine.ReleaseVelocity(-60));
        Assert.Equal(0, machine.ReleaseVelocity(0));
    }

    [Fact]
    public void Drop_reports_the_committed_index_and_retires_the_gesture()
    {
        var machine = NewMachine(grabIndex: 0);
        machine.Press(20);
        machine.Begin(40);
        machine.Evaluate(69);

        Assert.Equal(1, machine.Drop());
        Assert.Equal(TabDragPhase.Idle, machine.Phase);

        // A release that never lifted past the threshold was a click.
        var click = NewMachine();
        click.Press(20);
        Assert.Equal(-1, click.Drop());
    }

    [Fact]
    public void Cancel_retires_the_gesture_from_any_phase()
    {
        var pressed = NewMachine();
        pressed.Press(20);
        pressed.Cancel();
        Assert.Equal(TabDragPhase.Idle, pressed.Phase);

        var dragging = NewMachine();
        dragging.Press(20);
        dragging.Begin(40);
        dragging.Cancel();
        Assert.Null(dragging.Evaluate(1000));
        Assert.Equal(TabDragPhase.Idle, dragging.Phase);
    }

    [Fact]
    public void A_one_row_strip_has_nothing_to_reorder()
    {
        Assert.Throws<ArgumentException>(() => new TabDragReorder(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TabDragReorder(4, 4));
    }

    [Fact]
    public void Motion_gate_collapses_under_high_contrast_or_systems_with_animation_off()
    {
        Assert.True(TabStripMotion.Enabled(animationsEnabled: true, highContrast: false));
        Assert.False(TabStripMotion.Enabled(animationsEnabled: true, highContrast: true));
        Assert.False(TabStripMotion.Enabled(animationsEnabled: false, highContrast: false));
        Assert.False(TabStripMotion.Enabled(animationsEnabled: false, highContrast: true));
    }
}
