using System;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The hold-with-a-ring join gesture's clock.
///
/// Every test here drives the clock itself, and that is the point rather
/// than a convenience: a dwell asserted with a wall-clock budget on the
/// thread pool is a flake factory, and this repo has already paid for
/// three of them. The machine takes the time as an argument precisely so
/// the question "did the ring complete" is a fact a test states instead
/// of a race it hopes to win -- the same reason the test seam pins a
/// virtual clock for the length of one gesture.
/// </summary>
public class TabJoinDwellTests
{
    // Distinct reference identities, because the machine matches targets
    // by reference and nothing else; a value type here would let two
    // equal-but-different targets read as one.
    private sealed class Row
    {
        public required string Name;
        public override string ToString() => Name;
    }

    private static readonly Row A = new() { Name = "a" };
    private static readonly Row B = new() { Name = "b" };

    private static TabJoinDwell NewDwell() => new(dwellMs: 450, jitterPx: 3);

    [Fact]
    public void A_fresh_hold_starts_the_ring_empty_and_unarmed()
    {
        var dwell = NewDwell();
        Assert.False(dwell.Hold(A, position: 100, ms: 1000));
        Assert.Equal(0, dwell.Progress);
        Assert.False(dwell.IsArmed);
        Assert.Same(A, dwell.Target);
    }

    [Fact]
    public void The_ring_fills_in_proportion_to_the_time_held()
    {
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        dwell.Hold(A, 100, 1000 + 225);
        Assert.Equal(0.5, dwell.Progress, 3);
        Assert.False(dwell.IsArmed);
    }

    [Fact]
    public void The_ring_completes_at_the_dwell_and_arms_the_release()
    {
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        Assert.True(dwell.Hold(A, 100, 1000 + 450));
        Assert.Equal(1, dwell.Progress);
        Assert.True(dwell.IsArmed);
    }

    [Fact]
    public void A_release_before_the_dwell_leaves_the_ring_unarmed()
    {
        // The quick-release half of the contract: at one tick short of
        // the dwell the gesture is still a sort, and the strip reads
        // exactly this bit to decide.
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        Assert.False(dwell.Hold(A, 100, 1000 + 449));
        Assert.False(dwell.IsArmed);
    }

    [Fact]
    public void Travel_past_the_jitter_token_restarts_the_ring()
    {
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        dwell.Hold(A, 100, 1000 + 400);
        Assert.True(dwell.Progress > 0.8);
        // The hand moved on: the ring is a promise about a pointer that
        // has stopped, so it starts over rather than completing off the
        // travel that came before.
        dwell.Hold(A, 110, 1000 + 410);
        Assert.Equal(0, dwell.Progress);
        Assert.False(dwell.Hold(A, 110, 1000 + 440));
    }

    [Fact]
    public void Travel_inside_the_jitter_token_keeps_the_ring_filling()
    {
        // A resting hand is never perfectly still. If a pixel of noise
        // reset the ring it could never complete on real input.
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        dwell.Hold(A, 102, 1000 + 200);
        Assert.True(dwell.Progress > 0);
        Assert.True(dwell.Hold(A, 98, 1000 + 450));
    }

    [Fact]
    public void Moving_to_another_target_restarts_the_ring_on_that_one()
    {
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        dwell.Hold(A, 100, 1000 + 440);
        Assert.False(dwell.Hold(B, 140, 1000 + 450));
        Assert.Same(B, dwell.Target);
        Assert.Equal(0, dwell.Progress);
        Assert.True(dwell.Hold(B, 140, 1000 + 900));
    }

    [Fact]
    public void An_armed_ring_survives_the_hand_resting_but_not_a_new_target()
    {
        // Once the ring is full the promise is made; only leaving the
        // target takes it back, so a pixel of drift after the arm does
        // not silently turn a join back into a sort.
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        Assert.True(dwell.Hold(A, 100, 1000 + 450));
        Assert.True(dwell.Hold(A, 180, 1000 + 460));
        Assert.True(dwell.IsArmed);
        Assert.False(dwell.Hold(B, 180, 1000 + 470));
        Assert.False(dwell.IsArmed);
    }

    [Fact]
    public void A_null_target_withdraws_the_ring()
    {
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        Assert.True(dwell.Hold(A, 100, 1000 + 450));
        Assert.False(dwell.Hold(null, 100, 1000 + 460));
        Assert.Null(dwell.Target);
        Assert.Equal(0, dwell.Progress);
        Assert.False(dwell.IsArmed);
    }

    [Fact]
    public void Clear_disarms_so_one_gestures_ring_cannot_answer_the_next()
    {
        var dwell = NewDwell();
        dwell.Hold(A, 100, 1000);
        Assert.True(dwell.Hold(A, 100, 1000 + 450));
        dwell.Clear();
        Assert.False(dwell.IsArmed);
        Assert.Null(dwell.Target);
        // And the next hold on the same target starts from zero rather
        // than picking the old ring back up.
        Assert.False(dwell.Hold(A, 100, 1000 + 460));
        Assert.Equal(0, dwell.Progress);
    }

    [Fact]
    public void A_clock_that_went_backwards_restarts_rather_than_completing()
    {
        // The seam re-arms its virtual clock at every gesture, so a hold
        // can legitimately see a smaller ms than the one before. Read as
        // elapsed time that would be negative, and a clamp would report
        // the ring empty forever; read as a jump, it completes instantly.
        // Neither is a dwell, so the machine starts over.
        var dwell = NewDwell();
        dwell.Hold(A, 100, 5000);
        Assert.False(dwell.Hold(A, 100, 10));
        Assert.Equal(0, dwell.Progress);
        Assert.True(dwell.Hold(A, 100, 10 + 450));
    }

    [Fact]
    public void The_products_dwell_is_the_450ms_token_and_not_a_two_second_hold()
    {
        // The decision this gesture was settled on: the ring makes the
        // wait legible, so the wait is short. The default ctor is what
        // both strips construct, so the token is pinned through it.
        var dwell = new TabJoinDwell();
        dwell.Hold(A, 100, 0);
        Assert.False(dwell.Hold(A, 100, 449));
        Assert.True(dwell.Hold(A, 100, 450));
        Assert.Equal(450, TabStripMotion.JoinDwellMs);
    }

    [Fact]
    public void A_zero_dwell_is_refused_rather_than_arming_on_the_first_frame()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TabJoinDwell(0, 3));
    }
}
