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
    /// The motion gate: springs and glides run only when Windows
    /// animation effects are on and High Contrast is not. Disabled means
    /// every spring collapses to a cut; state correctness never waits on
    /// an animation completing.
    /// </summary>
    public static bool Enabled(bool animationsEnabled, bool highContrast)
        => animationsEnabled && !highContrast;
}
