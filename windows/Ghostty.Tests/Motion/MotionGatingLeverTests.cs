using System;
using Ghostty.Core.Motion;
using Ghostty.Core.Tabs;
using Ghostty.Motion;
using Ghostty.Services;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The user lever seat, as MotionGating's fallback applies it: the legacy
/// answer (system animations, high contrast) is what it is, and the lever
/// only ever makes things quieter. Off is outright; Reduced ceilings Full
/// at Reduced; FollowSystem passes the seats through. Pinned BY VALUE on
/// every row, never by call counts.
///
/// Rows carry the rungs as names because MotionPolicyLevel and
/// MotionSurfaceClass are internal to the app assembly and a public
/// theory cannot expose them in its signature; the parsers below are
/// exhaustive, so a renamed member fails loudly here.
/// </summary>
[Collection("PaneMotionSerial")]
public class MotionGatingLeverTests : IDisposable
{
    public MotionGatingLeverTests()
    {
        PaneMotion.ResetForTests();
        MotionGating.SetUserLeverSource(null);
        MotionGating.SetPowerSeatSource(null);
    }

    public void Dispose()
    {
        MotionGating.SetUserLeverSource(null);
        MotionGating.SetPowerSeatSource(null);
    }

    private static void UseLever(UserMotionLever lever)
        => MotionGating.SetUserLeverSource(() => lever);

    private static void UsePower(PowerSaverModeEx mode, PowerTriggersEx triggers)
        => MotionGating.SetPowerSeatSource(() => (mode, triggers));

    private static MotionPolicyLevel Level(string name) => name switch
    {
        nameof(MotionPolicyLevel.Full) => MotionPolicyLevel.Full,
        nameof(MotionPolicyLevel.Reduced) => MotionPolicyLevel.Reduced,
        nameof(MotionPolicyLevel.Off) => MotionPolicyLevel.Off,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown level"),
    };

    private static MotionSurfaceClass Surface(string name) => name switch
    {
        nameof(MotionSurfaceClass.PaneGeometry) => MotionSurfaceClass.PaneGeometry,
        nameof(MotionSurfaceClass.Chrome) => MotionSurfaceClass.Chrome,
        nameof(MotionSurfaceClass.Overlay) => MotionSurfaceClass.Overlay,
        nameof(MotionSurfaceClass.Ambient) => MotionSurfaceClass.Ambient,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown surface"),
    };

    [Theory]
    [InlineData(UserMotionLever.FollowSystem, "Full")]
    [InlineData(UserMotionLever.Reduced, "Reduced")]
    [InlineData(UserMotionLever.Off, "Off")]
    public void Each_rung_reports_itself_when_the_system_says_full(
        UserMotionLever lever, string expected)
    {
        UseLever(lever);

        // Quiet seats: system animations on, no high contrast. The lever
        // is the only seat speaking. Chrome too, because the strip is the
        // movement-bearing surface the gates have always carried.
        Assert.Equal(Level(expected), MotionGating.Effective(
            MotionSurfaceClass.Ambient, animationsEnabled: true, highContrast: false));
        Assert.Equal(Level(expected), MotionGating.Effective(
            MotionSurfaceClass.Chrome, animationsEnabled: true, highContrast: false));
    }

    [Fact]
    public void Without_a_source_the_fallback_defers_to_the_seats()
    {
        // The uninstalled state (a +version process, a fresh test run):
        // the legacy gates answer alone, exactly as before the lever.
        Assert.Equal(MotionPolicyLevel.Full, MotionGating.Effective(
            MotionSurfaceClass.Ambient, animationsEnabled: true, highContrast: false));
        Assert.Equal(MotionPolicyLevel.Off, MotionGating.Effective(
            MotionSurfaceClass.Ambient, animationsEnabled: false, highContrast: false));
    }

    [Theory]
    [InlineData("PaneGeometry")]
    [InlineData("Chrome")]
    [InlineData("Overlay")]
    [InlineData("Ambient")]
    public void The_lever_is_seat_wide_not_surface_local(string surface)
    {
        UseLever(UserMotionLever.Off);
        Assert.Equal(MotionPolicyLevel.Off, MotionGating.Effective(
            Surface(surface), animationsEnabled: true, highContrast: false));
    }

    /// <summary>
    /// The lattice: lever x the two seats the legacy gates already read,
    /// every cell resolved. Any Off lever forces Off whatever the seats
    /// say; FollowSystem hands the answer to the seats alone; Reduced
    /// keeps what the system still allows at Reduced and cannot raise
    /// anything the system already cut.
    /// </summary>
    [Theory]
    [InlineData(true, false, UserMotionLever.FollowSystem, "Full")]
    [InlineData(false, false, UserMotionLever.FollowSystem, "Off")]
    [InlineData(true, true, UserMotionLever.FollowSystem, "Off")]
    [InlineData(false, true, UserMotionLever.FollowSystem, "Off")]
    [InlineData(true, false, UserMotionLever.Off, "Off")]
    [InlineData(false, false, UserMotionLever.Off, "Off")]
    [InlineData(true, true, UserMotionLever.Off, "Off")]
    [InlineData(false, true, UserMotionLever.Off, "Off")]
    [InlineData(true, false, UserMotionLever.Reduced, "Reduced")]
    [InlineData(false, false, UserMotionLever.Reduced, "Off")]
    [InlineData(true, true, UserMotionLever.Reduced, "Off")]
    [InlineData(false, true, UserMotionLever.Reduced, "Off")]
    public void Lattice_lever_x_system_seats_resolves_most_severe_wins(
        bool animationsEnabled, bool highContrast, UserMotionLever lever,
        string expected)
    {
        UseLever(lever);
        Assert.Equal(Level(expected), MotionGating.Effective(
            MotionSurfaceClass.Ambient, animationsEnabled, highContrast));
    }

    /// <summary>
    /// The energy-saver seat fires the fallback's answer off: the mode
    /// can force it, any level trigger can fire it, and the remote-session
    /// bit rides in the composite but is masked OUT of the test - a
    /// transport state never moves the level.
    /// </summary>
    [Theory]
    [InlineData(PowerSaverModeEx.Always, PowerTriggersEx.None, "Off")]
    [InlineData(PowerSaverModeEx.Auto, PowerTriggersEx.BatterySaverOn, "Off")]
    [InlineData(PowerSaverModeEx.Auto, PowerTriggersEx.TransparencyEffectsOff, "Off")]
    [InlineData(PowerSaverModeEx.Never, PowerTriggersEx.TransparencyEffectsOff, "Off")]
    [InlineData(PowerSaverModeEx.Always, PowerTriggersEx.RemoteSession, "Off")]
    [InlineData(PowerSaverModeEx.Auto, PowerTriggersEx.RemoteSession, "Full")]
    [InlineData(PowerSaverModeEx.Never, PowerTriggersEx.None, "Full")]
    public void The_power_seat_fires_the_fallback(
        PowerSaverModeEx mode, PowerTriggersEx triggers, string expected)
    {
        UsePower(mode, triggers);
        Assert.Equal(Level(expected), MotionGating.Effective(
            MotionSurfaceClass.Ambient, animationsEnabled: true, highContrast: false));
    }

    /// <summary>Power saving is off, not reduced: a lever ceiling cannot
    /// raise what the seat has cut.</summary>
    [Theory]
    [InlineData(UserMotionLever.Reduced)]
    [InlineData(UserMotionLever.Off)]
    public void The_power_seat_beats_the_levers_ceiling(UserMotionLever lever)
    {
        UsePower(PowerSaverModeEx.Always, PowerTriggersEx.None);
        UseLever(lever);
        Assert.Equal(MotionPolicyLevel.Off, MotionGating.Effective(
            MotionSurfaceClass.Ambient, animationsEnabled: true, highContrast: false));
    }

    /// <summary>
    /// The strip is a movement-bearing surface: its route runs only when
    /// the resolved level is Full. Reduced keeps fades, not slides, so
    /// Reduced cuts the strip too; Off cuts it outright.
    /// </summary>
    [Theory]
    [InlineData(UserMotionLever.FollowSystem, true)]
    [InlineData(UserMotionLever.Reduced, false)]
    [InlineData(UserMotionLever.Off, false)]
    public void The_strip_route_runs_only_when_the_level_is_full(
        UserMotionLever lever, bool expected)
    {
        UseLever(lever);
        Assert.Equal(expected, TabStripMotion.Enabled(
            animationsEnabled: true, highContrast: false));
    }
}
