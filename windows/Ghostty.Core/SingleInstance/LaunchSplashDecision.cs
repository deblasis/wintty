using System;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// Whether the pre-XAML launch splash goes up for this launch.
///
/// <para>Split out of <see cref="SingleInstanceElection.ShouldShowLaunchSplash"/>
/// so the warm half can be decided without holding a real mutex. The election
/// owns "what kind of launch is this"; this owns "given that, does a splash
/// earn its keep". The election's property is the only production caller, and
/// the two-argument overload here is what a test drives, so the role table and
/// the warm rule are checkable without the OS.</para>
/// </summary>
internal static class LaunchSplashDecision
{
    /// <summary>
    /// Set before <c>Application.Start</c> by a layer that can answer whether
    /// a warm session is already sitting there ready to attach. The decision
    /// runs before WinUI exists, so whatever sits behind this must not need a
    /// DispatcherQueue, a config handle or a window: it is asked once, from
    /// <c>MainImpl</c>, on the way up.
    ///
    /// <para>Null by default, and null means "no idea, show the splash", which
    /// is the behaviour every launch has always had. A throw from the probe is
    /// read the same way, so a warm-session layer that is half built or fails
    /// to answer costs a splash rather than a black rectangle.</para>
    ///
    /// <para>Nothing in this repository sets it yet. It is the seam the warm
    /// session layer plugs into, and it is a static rather than a parameter
    /// because the site that reads it has no way to be handed one.</para>
    /// </summary>
    internal static Func<bool>? WarmSessionProbe { get; set; }

    /// <summary>Decide against the probe currently installed.</summary>
    internal static bool ShouldShow(SingleInstanceRole role)
        => ShouldShow(role, WarmSessionProbe);

    /// <summary>
    /// True unless a secondary is about to forward, where the splash would
    /// cover the primary's window, or a warm session is ready to attach, where
    /// it would only be a flash between two real frames.
    /// </summary>
    internal static bool ShouldShow(SingleInstanceRole role, Func<bool>? warmProbe)
    {
        if (role == SingleInstanceRole.Secondary) return false;
        if (warmProbe is null) return true;

        try
        {
            return !warmProbe();
        }
        catch (Exception)
        {
            // The probe is an optimisation; the splash is the guarantee. An
            // answer we cannot get is an answer we do not act on.
            return true;
        }
    }
}
