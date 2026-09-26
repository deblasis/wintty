using System;
using System.Linq;
using Ghostty.Core.Motion;
using Ghostty.Core.Power;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The motion policy's Ex mirrors are hand-kept copies of the real power
/// enums: the policy must stay plain, and the real enums carry the
/// config and monitor vocabulary. Nothing compile-time links the pairs,
/// so these pins ARE the link. The correspondence is BY NAME, not by
/// position, and two divergences are deliberate, pinned as divergences,
/// and load-bearing for the future adapter that maps real reads into
/// policy inputs:
///
/// - the mode mirror runs Never/Auto/Always (severity order) against the
///   config's Auto/Always/Never; and
/// - the trigger mirror assigns RemoteSession and TransparencyEffectsOff
///   the swapped bits.
///
/// A reorder, rename or renumber on either side reds here instead of
/// silently re-meaning the inputs the policy resolves on.
/// </summary>
public class MotionPolicyExParityTests
{
    private static string[] Names<T>() where T : struct, Enum
        => Enum.GetNames<T>();

    private static string[] Sorted<T>() where T : struct, Enum
        => Names<T>().OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void The_mode_mirror_names_exactly_the_real_mode_members()
    {
        Assert.Equal(Sorted<PowerSaverMode>(), Sorted<PowerSaverModeEx>());
    }

    [Fact]
    public void Both_mode_sides_pin_their_values_by_name()
    {
        // The config key's order: Auto, Always, Never.
        Assert.Equal(0, (int)PowerSaverMode.Auto);
        Assert.Equal(1, (int)PowerSaverMode.Always);
        Assert.Equal(2, (int)PowerSaverMode.Never);

        // The mirror's severity order: Never, Auto, Always. A deliberate
        // divergence from the config order, pinned as one.
        Assert.Equal(0, (int)PowerSaverModeEx.Never);
        Assert.Equal(1, (int)PowerSaverModeEx.Auto);
        Assert.Equal(2, (int)PowerSaverModeEx.Always);
    }

    [Fact]
    public void The_trigger_mirror_names_exactly_the_real_trigger_members()
    {
        Assert.Equal(Sorted<PowerSaverTrigger>(), Sorted<PowerTriggersEx>());
    }

    [Fact]
    public void Both_trigger_sides_pin_their_bits_by_name()
    {
        // The power monitor's bits: transparency-off rides below the
        // remote-session bit.
        Assert.Equal(0, (int)PowerSaverTrigger.None);
        Assert.Equal(1, (int)PowerSaverTrigger.BatterySaverOn);
        Assert.Equal(2, (int)PowerSaverTrigger.OnBattery);
        Assert.Equal(4, (int)PowerSaverTrigger.TransparencyEffectsOff);
        Assert.Equal(8, (int)PowerSaverTrigger.RemoteSession);

        // The mirror's bits: RemoteSession and TransparencyEffectsOff
        // deliberately swap values against the monitor, so an adapter
        // must map by name. This pin is what turns a value edit or an
        // inserted member on either side red.
        Assert.Equal(0, (int)PowerTriggersEx.None);
        Assert.Equal(1, (int)PowerTriggersEx.BatterySaverOn);
        Assert.Equal(2, (int)PowerTriggersEx.OnBattery);
        Assert.Equal(4, (int)PowerTriggersEx.RemoteSession);
        Assert.Equal(8, (int)PowerTriggersEx.TransparencyEffectsOff);
    }

    [Fact]
    public void Both_trigger_enums_stay_flag_sets()
    {
        Assert.True(typeof(PowerSaverTrigger).IsDefined(typeof(FlagsAttribute), inherit: false),
            "PowerSaverTrigger must stay [Flags]: callers pass the raw composite");
        Assert.True(typeof(PowerTriggersEx).IsDefined(typeof(FlagsAttribute), inherit: false),
            "PowerTriggersEx must stay [Flags]: the policy masks bits out of the composite");
    }
}
