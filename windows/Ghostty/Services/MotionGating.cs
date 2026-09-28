using System.Runtime.CompilerServices;
using Ghostty.Core.Motion;
using Ghostty.Core.Tabs;
using Ghostty.Motion;

namespace Ghostty.Services;

/// <summary>
/// The one place the shell's animation gates route through. Asked with
/// the surface family the caller is about to animate plus the two legacy
/// inputs the gates already read, it answers with the effective motion
/// level: a registered pane-motion coordinator's policy for that surface
/// when one is registered, and the motion truth table when none is. The
/// table merges the seats this side can see, most-severe-wins: the
/// energy-saver seat (installed over the power monitor), system
/// animations, high contrast, and the user's own Animations lever.
///
/// The install is the seam's whole wiring: Core cannot see this
/// assembly, so <see cref="Install"/> hands the routing down to the
/// strip gate's route slot at module load, before any surface can ask.
/// With no coordinator registered the route still answers exactly what
/// the gates answered before the seam existed; only a registered
/// coordinator can change an answer.
/// </summary>
internal static class MotionGating
{
    /// <summary>
    /// The effective motion level for one surface.
    /// <paramref name="animationsEnabled"/> and
    /// <paramref name="highContrast"/> are the gates' own reads; they
    /// feed the fallback's seats and are ignored whenever a coordinator
    /// is registered.
    /// </summary>
    public static MotionPolicyLevel Effective(MotionSurfaceClass surface, bool animationsEnabled, bool highContrast)
    {
        // One read of the registration, captured: Active is defined as
        // Current is not null, and the pattern holds the single read so a
        // concurrent reset can only fall through to the fallback answer.
        if (PaneMotion.Active && PaneMotion.Current is { } coordinator)
        {
            return coordinator.ResolvePolicy(surface);
        }

        return ToGateLevel(MotionPolicy.Resolve(FallbackInputs(animationsEnabled, highContrast)).Level);
    }

    /// <summary>
    /// The user's own Animations lever, read fresh on every ask. Defaults
    /// to FollowSystem so an unset source changes no answer; App installs
    /// a source over the persisted <c>animations</c> setting at startup.
    /// Applies on the fallback only: a registered coordinator owns its
    /// own answers.
    /// </summary>
    private static Func<UserMotionLever> _userLever = DefaultLever;

    internal static void SetUserLeverSource(Func<UserMotionLever>? source)
        => _userLever = source ?? DefaultLever;

    private static UserMotionLever DefaultLever() => UserMotionLever.FollowSystem;

    /// <summary>
    /// The energy-saver seat, in the truth table's mirror vocabulary. The
    /// trigger composite goes over raw, remote-session bit included: the
    /// table masks it out of the level test itself. Defaults to never
    /// firing; App installs a source over the power monitor at startup.
    /// Applies on the fallback only.
    /// </summary>
    private static Func<(PowerSaverModeEx Mode, PowerTriggersEx Triggers)> _powerSeat =
        DefaultPowerSeat;

    internal static void SetPowerSeatSource(
        Func<(PowerSaverModeEx Mode, PowerTriggersEx Triggers)>? source)
        => _powerSeat = source ?? DefaultPowerSeat;

    private static (PowerSaverModeEx Mode, PowerTriggersEx Triggers) DefaultPowerSeat()
        => (PowerSaverModeEx.Never, PowerTriggersEx.None);

    /// <summary>
    /// The fallback's inputs, as the table carries them: the two seats
    /// the gates have always read, the power and lever sources, and no
    /// hardware read. The hardware ceiling is pro-side by architecture;
    /// this side carries no probe and passes Unspecified. The fallback
    /// reports no pane-scale allowance, so the transport input is false;
    /// the remote-session bit itself rides in the composite and the
    /// table masks it out of the level test.
    /// </summary>
    private static MotionPolicyInputs FallbackInputs(bool animationsEnabled, bool highContrast)
    {
        var (mode, triggers) = _powerSeat();
        return new MotionPolicyInputs(
            PowerMode: mode,
            PowerTriggers: triggers,
            IsRemoteSession: false,
            SystemAnimationsEnabled: animationsEnabled,
            HighContrastApplied: highContrast,
            Hardware: HardwareCeiling.Unspecified,
            Lever: _userLever());
    }

    private static MotionPolicyLevel ToGateLevel(ResolvedMotionLevel level) => level switch
    {
        ResolvedMotionLevel.Reduced => MotionPolicyLevel.Reduced,
        ResolvedMotionLevel.Off => MotionPolicyLevel.Off,
        _ => MotionPolicyLevel.Full,
    };

    /// <summary>
    /// Wires the strip gate to this seam, once, at module load. The strip
    /// is window chrome and movement-bearing: slides, lifts, flights. Its
    /// route runs only on a Full answer. Reduced keeps fades, not slides,
    /// so a Reduced level cuts the strip too, and Off cuts it outright.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
        => TabStripMotion.Route = (animationsEnabled, highContrast) =>
            Effective(MotionSurfaceClass.Chrome, animationsEnabled, highContrast)
                == MotionPolicyLevel.Full;
}
