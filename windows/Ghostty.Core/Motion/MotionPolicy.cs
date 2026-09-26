using System;

namespace Ghostty.Core.Motion;

/// <summary>
/// The user-facing power-saver mode the motion policy reacts to. Mirrors
/// the config key's three values.
/// </summary>
public enum PowerSaverModeEx
{
    /// <summary>Never fire the energy-saver seat from the mode alone.</summary>
    Never,

    /// <summary>Follow the OS signals carried in <see cref="PowerTriggersEx"/>.</summary>
    Auto,

    /// <summary>Force the energy-saver seat on, regardless of OS signals.</summary>
    Always,
}

/// <summary>
/// The individual OS signals that can contribute to the energy-saver seat.
/// Mirrors the power monitor's trigger set, flags so callers pass the raw
/// composite. Note that <see cref="RemoteSession"/> rides in this composite
/// but the policy masks it out of the energy-saver test: transport state
/// never moves the level.
/// </summary>
[Flags]
public enum PowerTriggersEx
{
    None = 0,
    BatterySaverOn = 1,
    OnBattery = 2,
    RemoteSession = 4,
    TransparencyEffectsOff = 8,
}

/// <summary>
/// How demanding the rendering surface is. Only <see cref="EssentialSource"/>
/// carries policy semantics: it ceilings the resolved level at Reduced.
/// </summary>
public enum HardwareCeiling
{
    /// <summary>No hardware read, or nothing worth acting on.</summary>
    Unspecified,

    /// <summary>An ordinary rendering surface.</summary>
    Standard,

    /// <summary>A weak rendering surface, still able to animate.</summary>
    Low,

    /// <summary>A surface that must not be given animation work.</summary>
    EssentialSource,
}

/// <summary>
/// The user's own motion lever. It sits under every automatic seat:
/// <see cref="Off"/> forces Off outright, <see cref="Reduced"/> ceilings at
/// Reduced, <see cref="FollowSystem"/> defers to what the system seats say.
/// </summary>
public enum UserMotionLever
{
    FollowSystem,
    Reduced,
    Off,
}

/// <summary>
/// Everything the motion policy merges, as plain values. Reading the OS
/// sources (the animation and accessibility settings, the power monitor,
/// the hardware probe) is the caller's job; this record carries only what
/// those reads produced, which is what keeps the policy pure and testable
/// headlessly.
/// </summary>
/// <param name="PowerMode">The user-facing power-saver mode.</param>
/// <param name="PowerTriggers">
/// The raw trigger composite, including <see cref="PowerTriggersEx.RemoteSession"/>.
/// The policy masks RemoteSession out of the energy-saver test itself.
/// </param>
/// <param name="IsRemoteSession">
/// The transport bit on its own. It never enters the resolved level; it is
/// reported beside it as <see cref="ResolvedMotion.PaneScaleMotionAllowed"/>.
/// </param>
/// <param name="SystemAnimationsEnabled">
/// The OS "animate elements" read. When the source is unreadable the CALLER
/// maps that to <c>true</c> (fail-open: an unreadable state must not
/// silently disable animation). The mapping happens at the call site; this
/// record only ever sees the resulting bool.
/// </param>
/// <param name="HighContrastApplied">
/// The composed high-contrast read (should apply), never the raw active
/// toggle.
/// </param>
/// <param name="Hardware">The surface-state flag; see <see cref="HardwareCeiling"/>.</param>
/// <param name="Lever">The user's own lever; see <see cref="UserMotionLever"/>.</param>
public sealed record MotionPolicyInputs(
    PowerSaverModeEx PowerMode,
    PowerTriggersEx PowerTriggers,
    bool IsRemoteSession,
    bool SystemAnimationsEnabled,
    bool HighContrastApplied,
    HardwareCeiling Hardware,
    UserMotionLever Lever);

/// <summary>The resolved levels, ordered from least to most severe.</summary>
public enum ResolvedMotionLevel
{
    Full,
    Reduced,
    Off,
}

/// <summary>
/// The policy's outcome: one resolved level, plus the pane-scale allowance
/// the transport state decides on its own (a remote session bars pane-scale
/// motion without touching the level).
/// </summary>
public sealed record ResolvedMotion(ResolvedMotionLevel Level, bool PaneScaleMotionAllowed);

/// <summary>
/// The motion policy truth table: merges system animation state, high
/// contrast, power and transport state, and the user's lever into one
/// resolved level, so gating decisions are data and testable headlessly.
///
/// The seats, in order, merged most-severe-wins (Full &lt; Reduced &lt; Off):
/// 1. Energy saver: fires when the mode is Always or any of
///    BatterySaverOn / OnBattery / TransparencyEffectsOff is set.
///    RemoteSession is masked out of this test. Power saving on resolves
///    fully off: not reduced - off.
/// 2. System animations disabled at the OS.
/// 3. High contrast applied.
/// 4. EssentialSource hardware ceilings the level at Reduced. The remote
///    session never enters the level; it only bars pane-scale motion,
///    reported beside the level on the result.
/// 5. The user lever: Off forces Off outright, Reduced ceilings at
///    Reduced, FollowSystem defers to the seats above.
///
/// A pure function: no IO, no statics, no clock.
/// </summary>
public static class MotionPolicy
{
    /// <summary>The triggers that fire the energy-saver seat. RemoteSession is deliberately absent.</summary>
    private const PowerTriggersEx LevelTriggers =
        PowerTriggersEx.BatterySaverOn | PowerTriggersEx.OnBattery | PowerTriggersEx.TransparencyEffectsOff;

    public static ResolvedMotion Resolve(MotionPolicyInputs inputs)
    {
        var level = ResolvedMotionLevel.Full;

        // Seat 1: energy saver. The mode can force it, and the composite
        // can fire it, with RemoteSession masked out of the test. The law:
        // power saving on means animations off. Not reduced - off.
        if (inputs.PowerMode == PowerSaverModeEx.Always
            || (inputs.PowerTriggers & LevelTriggers) != 0)
        {
            level = ResolvedMotionLevel.Off;
        }

        // Seat 2: the OS has animations off; nothing animates.
        if (!inputs.SystemAnimationsEnabled)
        {
            level = ResolvedMotionLevel.Off;
        }

        // Seat 3: high contrast is applied; animation reads as noise.
        if (inputs.HighContrastApplied)
        {
            level = ResolvedMotionLevel.Off;
        }

        // Seat 4: essential-source hardware ceilings the level at Reduced.
        // A ceiling can only pull Full down; an already-more-severe level
        // stays where it is.
        if (inputs.Hardware == HardwareCeiling.EssentialSource
            && level == ResolvedMotionLevel.Full)
        {
            level = ResolvedMotionLevel.Reduced;
        }

        // Seat 5: the user lever. Off is outright; Reduced is a ceiling.
        switch (inputs.Lever)
        {
            case UserMotionLever.Off:
                level = ResolvedMotionLevel.Off;
                break;
            case UserMotionLever.Reduced:
                if (level == ResolvedMotionLevel.Full)
                {
                    level = ResolvedMotionLevel.Reduced;
                }

                break;
        }

        return new ResolvedMotion(level, PaneScaleMotionAllowed: !inputs.IsRemoteSession);
    }
}
