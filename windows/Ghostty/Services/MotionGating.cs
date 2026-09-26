using System.Runtime.CompilerServices;
using Ghostty.Core.Tabs;
using Ghostty.Motion;

namespace Ghostty.Services;

/// <summary>
/// The one place the shell's animation gates route through. Asked with
/// the surface family the caller is about to animate plus the two legacy
/// inputs the gates already read, it answers with the effective motion
/// level: a registered pane-motion coordinator's policy for that surface
/// when one is registered, and the legacy truth table (system animations
/// on and high contrast not applied means Full, anything else Off) when
/// none is.
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
    /// decide the legacy fallback and are ignored whenever a coordinator
    /// is registered.
    /// </summary>
    public static MotionPolicyLevel Effective(MotionSurfaceClass surface, bool animationsEnabled, bool highContrast)
    {
        // One read of the registration, captured: Active is defined as
        // Current is not null, and the pattern holds the single read so a
        // concurrent reset can only fall through to the legacy answer.
        if (PaneMotion.Active && PaneMotion.Current is { } coordinator)
        {
            return coordinator.ResolvePolicy(surface);
        }

        return TabStripMotion.Legacy(animationsEnabled, highContrast)
            ? MotionPolicyLevel.Full
            : MotionPolicyLevel.Off;
    }

    /// <summary>
    /// Wires the strip gate to this seam, once, at module load. The strip
    /// is window chrome, so its route asks for
    /// <see cref="MotionSurfaceClass.Chrome"/>, and a level counts as on
    /// for the strip unless it is Off: Reduced still runs, and the
    /// coordinator that wants the strip silent says Off.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
        => TabStripMotion.Route = (animationsEnabled, highContrast) =>
            Effective(MotionSurfaceClass.Chrome, animationsEnabled, highContrast)
                != MotionPolicyLevel.Off;
}
