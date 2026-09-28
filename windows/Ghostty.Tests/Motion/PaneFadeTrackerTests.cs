using System;
using System.Collections.Generic;
using Ghostty.Core.Motion;
using Ghostty.Core.Panes;
using Ghostty.Motion;
using Ghostty.Services;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The pane fades' lifecycle law, over the tracker the shell's tree
/// mutations ride: one fade per pane at a time, a natural finish clears
/// its own flight, and the rebuild law -- every fade standing is ended
/// through the settle write before anything touches the tree, which is
/// how a control is never remounted mid-fade and every settled pane
/// reads opacity 1 again.
///
/// The tracker is plain C# (it lives in the Motion folder the test
/// assembly compiles in), so the law itself runs here; the WinUI half --
/// the boards, the restore write's real target, the call-site placement
/// -- is pinned on the source by Wiring.PaneFadeWiringTests, and the
/// census pins the routing. The gating leg at the bottom is the
/// fade-bearing route itself: the fades ask it, so reduced keeping them
/// and off cutting them is a property of what the gate answers, asserted
/// here rather than assumed.
/// </summary>
[Collection("PaneMotionSerial")]
public sealed class PaneFadeTrackerTests : IDisposable
{
    /// <summary>What the settle write would land on: a control's two
    /// fade-relevant properties, held so the installed write can be
    /// watched doing exactly what the real one does.</summary>
    private sealed class FakeTarget
    {
        public double Opacity;
        public bool HitTestVisible;
    }

    private readonly Dictionary<LeafPane, FakeTarget> _targets = new();
    private readonly List<(LeafPane Leaf, PaneFadeKind Kind)> _settleWrites = new();

    public PaneFadeTrackerTests()
    {
        PaneMotion.ResetForTests();
        MotionGating.SetUserLeverSource(null);
        MotionGating.SetPowerSeatSource(null);
        PaneFadeTracker.Clear();

        // The settle write, shaped as PaneHost installs it: restore the
        // control the fade was standing on.
        PaneFadeTracker.Settle = (leaf, kind) =>
        {
            _settleWrites.Add((leaf, kind));
            if (_targets.TryGetValue(leaf, out var t))
            {
                t.Opacity = 1;
                if (kind == PaneFadeKind.SoftCloseOut) t.HitTestVisible = true;
            }
        };
    }

    public void Dispose()
    {
        PaneFadeTracker.Settle = null;
        PaneFadeTracker.Clear();
        MotionGating.SetUserLeverSource(null);
        MotionGating.SetPowerSeatSource(null);
    }

    private LeafPane Fading(PaneFadeKind kind, double midFadeOpacity = 0.5)
    {
        var leaf = new LeafPane();
        _targets[leaf] = new FakeTarget { Opacity = midFadeOpacity };
        Assert.True(PaneFadeTracker.Begin(leaf, kind));
        return leaf;
    }

    // -- The flight law ---------------------------------------------------

    [Fact]
    public void A_begin_records_the_flight_and_a_second_begin_is_refused()
    {
        var leaf = new LeafPane();

        Assert.True(PaneFadeTracker.Begin(leaf, PaneFadeKind.SplitIn));
        Assert.True(PaneFadeTracker.IsFading(leaf));
        Assert.Equal(PaneFadeKind.SplitIn, PaneFadeTracker.KindOf(leaf));
        Assert.Equal(1, PaneFadeTracker.Standing);

        // One fade per pane: the standing one finishes as it is.
        Assert.False(PaneFadeTracker.Begin(leaf, PaneFadeKind.SoftCloseOut));
        Assert.Equal(PaneFadeKind.SplitIn, PaneFadeTracker.KindOf(leaf));
        Assert.Equal(1, PaneFadeTracker.Standing);
    }

    [Fact]
    public void None_begins_nothing()
    {
        var leaf = new LeafPane();

        Assert.False(PaneFadeTracker.Begin(leaf, PaneFadeKind.None));
        Assert.False(PaneFadeTracker.IsFading(leaf));
        Assert.Equal(0, PaneFadeTracker.Standing);
    }

    [Fact]
    public void A_natural_finish_clears_its_own_flight()
    {
        var leaf = Fading(PaneFadeKind.SplitIn);

        PaneFadeTracker.Settled(leaf);

        Assert.False(PaneFadeTracker.IsFading(leaf));
        Assert.Equal(PaneFadeKind.None, PaneFadeTracker.KindOf(leaf));
        Assert.Equal(0, PaneFadeTracker.Standing);
    }

    [Fact]
    public void A_settled_leaf_can_fade_again()
    {
        var leaf = Fading(PaneFadeKind.SplitIn);
        PaneFadeTracker.Settled(leaf);

        Assert.True(PaneFadeTracker.Begin(leaf, PaneFadeKind.SoftCloseOut));
        Assert.Equal(PaneFadeKind.SoftCloseOut, PaneFadeTracker.KindOf(leaf));
    }

    // -- The rebuild law ---------------------------------------------------

    /// <summary>
    /// The delicate rule, at the seam the tree mutations call: two fades
    /// standing (a split's fade-in, a soft close's fade-away), both ended
    /// out of turn by one SettleAll -- the write lands on every flight
    /// with its own kind, the control reads opacity 1 again (1.0, not the
    /// mid-fade value it was caught at), and nothing is left standing.
    /// </summary>
    [Fact]
    public void SettleAll_ends_every_flight_through_the_settle_write()
    {
        var split = Fading(PaneFadeKind.SplitIn, midFadeOpacity: 0.25);
        var closing = Fading(PaneFadeKind.SoftCloseOut, midFadeOpacity: 0.6);
        Assert.Equal(2, PaneFadeTracker.Standing);

        var ended = PaneFadeTracker.SettleAll();

        Assert.Equal(2, ended.Count);
        Assert.Contains((split, PaneFadeKind.SplitIn), ended);
        Assert.Contains((closing, PaneFadeKind.SoftCloseOut), ended);

        // The write saw both flights, each with its own kind.
        Assert.Equal(2, _settleWrites.Count);
        Assert.Contains((split, PaneFadeKind.SplitIn), _settleWrites);
        Assert.Contains((closing, PaneFadeKind.SoftCloseOut), _settleWrites);

        // And the controls read the way a rebuild may mount them: opacity
        // 1, the soft close's pane taking hits again, the split's pane
        // never touched (its fade never pulled that flag).
        Assert.Equal(1, _targets[split].Opacity);
        Assert.Equal(1, _targets[closing].Opacity);
        Assert.False(_targets[split].HitTestVisible);
        Assert.True(_targets[closing].HitTestVisible);

        Assert.Equal(0, PaneFadeTracker.Standing);
    }

    [Fact]
    public void SettleAll_with_nothing_standing_writes_nothing()
    {
        Assert.Empty(PaneFadeTracker.SettleAll());
        Assert.Empty(_settleWrites);
    }

    [Fact]
    public void SettleAll_clears_even_without_an_installed_write()
    {
        // The tracker runs wherever its assembly runs, shell or not; the
        // write is the shell's half. Without one the law still holds:
        // every flight ends, nothing stands, nothing crashes.
        PaneFadeTracker.Settle = null;
        var leaf = Fading(PaneFadeKind.SoftCloseOut);

        var ended = PaneFadeTracker.SettleAll();

        Assert.Single(ended);
        Assert.Equal(0, PaneFadeTracker.Standing);
    }

    // -- The cap ------------------------------------------------------------

    [Fact]
    public void The_fade_cap_is_short_and_positive()
    {
        Assert.True(
            PaneFadeTracker.FadeMs > 0 && PaneFadeTracker.FadeMs <= 120,
            $"the pane fade cap is {PaneFadeTracker.FadeMs}ms; it must not "
            + "exceed 120ms");
    }

    // -- The route the fades ask ---------------------------------------------

    private static bool FadeGate(bool animationsEnabled, UserMotionLever lever)
    {
        MotionGating.SetUserLeverSource(() => lever);
        return MotionGating.Effective(
            MotionSurfaceClass.PaneGeometry, animationsEnabled, highContrast: false)
            != MotionPolicyLevel.Off;
    }

    /// <summary>
    /// The fades ride the fade-bearing route (the level is not Off), the
    /// same law the bell fade and the strip's fades ride. Reduced keeps
    /// fades and removes movement, and these ARE fades -- so Reduced
    /// runs them where the strip's movement route would cut; Off -- the
    /// lever's, the seat's, or the system's -- cuts them to the plain
    /// cut.
    /// </summary>
    [Theory]
    [InlineData(true, UserMotionLever.FollowSystem, true)]
    [InlineData(true, UserMotionLever.Reduced, true)]
    [InlineData(true, UserMotionLever.Off, false)]
    [InlineData(false, UserMotionLever.FollowSystem, false)]
    [InlineData(false, UserMotionLever.Reduced, false)]
    [InlineData(false, UserMotionLever.Off, false)]
    public void The_fade_gate_runs_under_reduced_and_cuts_at_off(
        bool animationsEnabled, UserMotionLever lever, bool expected)
        => Assert.Equal(expected, FadeGate(animationsEnabled, lever));

    [Fact]
    public void The_power_seat_cuts_the_fade_gate()
    {
        MotionGating.SetUserLeverSource(() => UserMotionLever.FollowSystem);
        MotionGating.SetPowerSeatSource(
            () => (PowerSaverModeEx.Always, PowerTriggersEx.None));

        Assert.False(MotionGating.Effective(
            MotionSurfaceClass.PaneGeometry, animationsEnabled: true, highContrast: false)
            != MotionPolicyLevel.Off);
    }

    /// <summary>
    /// The seam itself honours high contrast when it is asked with it
    /// (Off is the answer, and a plain cut with it). The route helper the
    /// fades ask passes high contrast as false -- its own documented
    /// pre-existing law, the bell fade's law, pinned where that helper is
    /// wired; this row exists so the difference is a stated fact, not a
    /// silently different gate.
    /// </summary>
    [Fact]
    public void The_seam_cuts_the_fade_gate_under_high_contrast_when_asked()
    {
        Assert.Equal(
            MotionPolicyLevel.Off,
            MotionGating.Effective(
                MotionSurfaceClass.PaneGeometry, animationsEnabled: true, highContrast: true));
    }
}
