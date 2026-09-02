namespace Ghostty.Core.Tabs;

/// <summary>
/// Motion constants for the tab strips, and the reduce-motion gate.
///
/// Numbers come from the strip-motion spec's token table, which in turn
/// takes durations from the Windows 11 signature-experiences table and
/// spring parameters from the Windows Design team's published spring
/// table. The WinRT spring classes document no defaults, so every
/// DampingRatio and Period here is written explicitly at the use site.
///
/// Lives in Core, next to the drag state machine, so the numbers and
/// the gate truth table are pinnable by tests without a WinUI host;
/// the shell reads them through InternalsVisibleTo.
/// </summary>
internal static class TabStripMotion
{
    /// <summary>
    /// Vertical pointer travel that turns a press into a drag. Below it
    /// the gesture is a click and activation stays click-driven.
    /// </summary>
    public const double GrabStartThresholdPx = 4;

    /// <summary>
    /// Travel past a neighbour's center, beyond the crossing itself,
    /// before the reorder commits: a wobbly hand must not oscillate the
    /// order back and forth.
    /// </summary>
    public const double CrossingHysteresisPx = 8;

    // Autoscroll band at the scroller edges: 360 px/s from 24px away,
    // ramping to 840 px/s inside 8px.
    public const double AutoscrollBandPx = 24;
    public const double AutoscrollInnerBandPx = 8;
    public const double AutoscrollBasePxPerSecond = 360;
    public const double AutoscrollMaxPxPerSecond = 840;

    /// <summary>Trailing window of pointer samples release velocity is read from.</summary>
    public const double VelocityWindowMs = 100;

    /// <summary>
    /// The drop settle: the only inertia spring in the interaction. The
    /// dropped row lands with the velocity the hand gave it; neighbours
    /// never carry release velocity.
    /// </summary>
    public const float SettleDampingRatio = 0.80f;
    public const double SettlePeriodMs = 50;

    /// <summary>Neighbour gap motion: a short eased glide.</summary>
    public const double GapGlideMs = 250;

    /// <summary>Row lift on grab: a near-critical scale spring to a 3% grow.</summary>
    public const float LiftDampingRatio = 0.70f;
    public const double LiftPeriodMs = 50;
    public const float LiftScale = 1.03f;

    /// <summary>
    /// The pin flight: the ghost travels from the released row to the slot
    /// it promised, on the neighbours' Existing Elements curve. The flight
    /// is programmatic -- it starts at velocity 0, never the gesture's.
    /// </summary>
    public const double PinFlightMs = 250;

    /// <summary>
    /// The landing's one visible bounce: the bounciest tier in the strip,
    /// spent on the ghost because a programmatic flourish is the one place
    /// an overshoot reads as delight instead of slop. DampingRatio and
    /// Period are written explicitly -- the WinRT spring classes document
    /// no defaults.
    /// </summary>
    public const float PinSettleDampingRatio = 0.6f;
    public const double PinSettlePeriodMs = 60;

    /// <summary>The Fade token: ghost handoff and header crossfades.</summary>
    public const double FadeMs = 83;

    /// <summary>
    /// The active tab settling into the field: its fill eases from the
    /// chrome it was wearing to the terminal's own ground, and the seam
    /// cover rides the same brush instance, so the join cannot split for a
    /// single frame of the flight.
    ///
    /// The table's 167ms rung -- one above Fade, because this is a surface
    /// changing what it is rather than an element appearing or leaving.
    /// </summary>
    public const double FieldSettleMs = 167;

    /// <summary>
    /// The join gesture's dwell: how long a dragged row must be held over
    /// a neighbour before the ring completes and the release joins the
    /// two into a group. Deliberately far under the couple of seconds a
    /// hold-to-confirm usually costs -- the ring is filling the whole
    /// time, so the wait only has to outlast a hand passing through.
    /// </summary>
    public const double JoinDwellMs = 450;

    /// <summary>
    /// Travel along the axis that restarts the dwell. The ring is a
    /// promise about a pointer that has STOPPED; without this a drag
    /// flicked down the strip would ring on whatever row it happened to
    /// be over for a frame.
    /// </summary>
    public const double JoinJitterPx = 3;

    /// <summary>
    /// How close to a neighbour's arranged center the dragged row must
    /// come to count as being OVER it, as a fraction of the pitch to that
    /// neighbour. Half a pitch: the row has travelled most of the way
    /// there. A row at rest in its own slot is a full pitch away, so it
    /// targets nothing.
    /// </summary>
    public const double JoinBandFraction = 0.5;

    /// <summary>
    /// The ring's own frame: it fills on a clock, not on pointer events,
    /// because a hand held perfectly still raises none and a ring that
    /// only advanced on motion could never complete. Every tick is a
    /// property set on one small element, so the fill IS the dwell's
    /// progress and the two cannot drift apart.
    /// </summary>
    public const double JoinRingTickMs = 16;

    /// <summary>The ring itself: a circle over the target's center, stroked thin.</summary>
    public const double JoinRingDiameterPx = 22;
    public const double JoinRingStrokePx = 2;

    /// <summary>
    /// The completed ring hands off to a halo over the target row: the
    /// moment the release changes meaning has to be a thing the eye
    /// catches, not a number only the code knows. The pop is the one
    /// flourish the gesture spends, on the lift's spring family.
    /// </summary>
    public const double JoinHaloOpacity = 0.30;
    public const float JoinArmDampingRatio = 0.60f;
    public const double JoinArmPeriodMs = 60;
    public const float JoinArmRingScale = 1.15f;

    /// <summary>
    /// The horizontal drag's handback: the lifted tab's shadow fades out
    /// on this clock while its scale springs down. The spring is the
    /// landing; the fade is what keeps the shadow from popping off a tab
    /// that is still visibly settling.
    /// </summary>
    public const double UnliftFadeMs = 83;

    /// <summary>
    /// The lifted tab's shadow: the one depth cue the horizontal drag
    /// spends. BlurRadius, offset, and opacity are written explicitly at
    /// the use site.
    /// </summary>
    public const double LiftShadowBlurRadiusPx = 16;
    public const double LiftShadowOffsetYPx = 4;
    public const float LiftShadowOpacity = 0.25f;

    /// <summary>
    /// The motion gate: springs and glides run only when Windows
    /// animation effects are on and High Contrast is not. Disabled means
    /// every spring collapses to a cut; state correctness never waits on
    /// an animation completing.
    ///
    /// The strip's sources for the two flags: animationsEnabled is
    /// UISettings.AnimationsEnabled (UISettings is thread-affine -- read
    /// it on the dispatcher); highContrast is
    /// HighContrastDetector.IsActive() COMPOSED through
    /// HighContrastState.ShouldApply, because raw IsActive diverges from
    /// the chrome whenever the user has opted out of High Contrast
    /// themes. There is no existing animation-preference read anywhere
    /// else in the repo to reuse.
    /// </summary>
    public static bool Enabled(bool animationsEnabled, bool highContrast)
        => animationsEnabled && !highContrast;
}
