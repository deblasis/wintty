using Ghostty.Core.Motion;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The lattice for <see cref="MotionPolicy"/>: every seat on and off on its
/// own, then the conflict rows where several seats fire at once and the
/// most-severe merge has to hold. Pure xunit: no UI, no clock, no sleeps.
/// </summary>
public class MotionPolicyTests
{
    /// <summary>
    /// One quiet baseline, then each test names only the seats it moves.
    /// The defaults are: Auto power mode, no triggers, no remote session,
    /// system animations enabled, no high contrast, unspecified hardware,
    /// and a FollowSystem lever.
    /// </summary>
    private static MotionPolicyInputs Inputs(
        PowerSaverModeEx? powerMode = null,
        PowerTriggersEx triggers = PowerTriggersEx.None,
        bool isRemoteSession = false,
        bool systemAnimationsEnabled = true,
        bool highContrastApplied = false,
        HardwareCeiling hardware = HardwareCeiling.Unspecified,
        UserMotionLever lever = UserMotionLever.FollowSystem)
    {
        return new MotionPolicyInputs(
            powerMode ?? PowerSaverModeEx.Auto,
            triggers,
            isRemoteSession,
            systemAnimationsEnabled,
            highContrastApplied,
            hardware,
            lever);
    }

    [Fact]
    public void All_seats_quiet_resolves_full_and_allows_pane_scale()
    {
        // This row is also the fail-open pin: SystemAnimationsEnabled: true
        // is what the caller feeds when the OS source is unreadable (fail
        // open happens at the call site, not in the policy), and the value
        // that mapping feeds in must gate nothing.
        var resolved = MotionPolicy.Resolve(Inputs());

        Assert.Equal(ResolvedMotionLevel.Full, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Theory]
    [InlineData(PowerTriggersEx.BatterySaverOn)]
    [InlineData(PowerTriggersEx.OnBattery)]
    [InlineData(PowerTriggersEx.TransparencyEffectsOff)]
    public void Energy_saver_trigger_routes_resolve_reduced(PowerTriggersEx trigger)
    {
        var resolved = MotionPolicy.Resolve(Inputs(triggers: trigger));

        Assert.Equal(ResolvedMotionLevel.Reduced, resolved.Level);
        // Power state alone says nothing about transport: the allowance
        // stays open.
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Energy_saver_always_mode_fires_without_any_trigger()
    {
        // The forced-mode trap: Always fires the seat on the mode alone,
        // with an empty trigger set. An implementation that only looks at
        // the composite reads this row as Full.
        var resolved = MotionPolicy.Resolve(Inputs(powerMode: PowerSaverModeEx.Always));

        Assert.Equal(ResolvedMotionLevel.Reduced, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Never_mode_has_no_branch_in_the_energy_saver_formula()
    {
        // The formula is verbatim "Always || any of the three masked
        // triggers": there is no Never branch in the policy. A caller that
        // wants Never to suppress the seat passes a cleared trigger set,
        // its own mapping decision, exactly like the fail-open animation
        // read. This row pins the formula as written so the behaviour is
        // explicit rather than accidental.
        var resolved = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Never,
            triggers: PowerTriggersEx.BatterySaverOn));

        Assert.Equal(ResolvedMotionLevel.Reduced, resolved.Level);
    }

    [Fact]
    public void Remote_session_trigger_alone_does_not_fire_energy_saver()
    {
        // The masked-bit trap: RemoteSession rides in the same composite,
        // but the energy-saver test masks it out. The level must not move,
        // and the transport must still be reported as barred beside it.
        var resolved = MotionPolicy.Resolve(Inputs(
            triggers: PowerTriggersEx.RemoteSession,
            isRemoteSession: true));

        Assert.Equal(ResolvedMotionLevel.Full, resolved.Level);
        Assert.False(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Remote_session_trigger_does_not_mask_out_real_triggers()
    {
        var resolved = MotionPolicy.Resolve(Inputs(
            triggers: PowerTriggersEx.BatterySaverOn | PowerTriggersEx.RemoteSession,
            isRemoteSession: true));

        Assert.Equal(ResolvedMotionLevel.Reduced, resolved.Level);
        Assert.False(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void System_animations_disabled_resolves_off()
    {
        var resolved = MotionPolicy.Resolve(Inputs(systemAnimationsEnabled: false));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void High_contrast_applied_resolves_off()
    {
        var resolved = MotionPolicy.Resolve(Inputs(highContrastApplied: true));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Remote_session_leaves_the_level_alone_and_bars_pane_scale()
    {
        // The remote-session bit never enters the level. It surfaces only
        // as the pane-scale allowance beside it.
        var resolved = MotionPolicy.Resolve(Inputs(isRemoteSession: true));

        Assert.Equal(ResolvedMotionLevel.Full, resolved.Level);
        Assert.False(resolved.PaneScaleMotionAllowed);
    }

    [Theory]
    [InlineData(HardwareCeiling.Unspecified, ResolvedMotionLevel.Full)]
    [InlineData(HardwareCeiling.Standard, ResolvedMotionLevel.Full)]
    [InlineData(HardwareCeiling.Low, ResolvedMotionLevel.Full)]
    [InlineData(HardwareCeiling.EssentialSource, ResolvedMotionLevel.Reduced)]
    public void Only_the_essential_source_ceiling_touches_the_level(
        HardwareCeiling hardware, ResolvedMotionLevel expected)
    {
        var resolved = MotionPolicy.Resolve(Inputs(hardware: hardware));

        Assert.Equal(expected, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Theory]
    [InlineData(UserMotionLever.FollowSystem, ResolvedMotionLevel.Full)]
    [InlineData(UserMotionLever.Reduced, ResolvedMotionLevel.Reduced)]
    [InlineData(UserMotionLever.Off, ResolvedMotionLevel.Off)]
    public void The_user_lever_resolves_by_itself_when_all_else_is_quiet(
        UserMotionLever lever, ResolvedMotionLevel expected)
    {
        var resolved = MotionPolicy.Resolve(Inputs(lever: lever));

        Assert.Equal(expected, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Every_severe_seat_at_once_still_resolves_off()
    {
        // The brief's conflict row: Always mode, a remote session, high
        // contrast applied, and the lever at Off.
        var resolved = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Always,
            isRemoteSession: true,
            highContrastApplied: true,
            lever: UserMotionLever.Off));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
        Assert.False(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Every_seat_firing_at_once_resolves_off()
    {
        var resolved = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Always,
            triggers: PowerTriggersEx.BatterySaverOn | PowerTriggersEx.OnBattery | PowerTriggersEx.TransparencyEffectsOff,
            isRemoteSession: true,
            systemAnimationsEnabled: false,
            highContrastApplied: true,
            hardware: HardwareCeiling.EssentialSource,
            lever: UserMotionLever.Off));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
        Assert.False(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Energy_saver_and_disabled_animations_merge_to_off()
    {
        // The row that separates most-severe merge from first-hit-wins:
        // seat 1 fires first, but the disabled animations are more severe
        // and must win.
        var resolved = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Always,
            systemAnimationsEnabled: false));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
    }

    [Fact]
    public void High_contrast_and_energy_saver_merge_to_off()
    {
        var resolved = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Always,
            highContrastApplied: true));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
    }

    [Fact]
    public void Lever_off_resolves_off_outright_when_nothing_else_fired()
    {
        var resolved = MotionPolicy.Resolve(Inputs(lever: UserMotionLever.Off));

        Assert.Equal(ResolvedMotionLevel.Off, resolved.Level);
        Assert.True(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Lever_reduced_cannot_raise_a_more_severe_merge()
    {
        // The lever's Reduced is a ceiling, not a floor: an Off produced by
        // any seat stays Off.
        Assert.Equal(
            ResolvedMotionLevel.Off,
            MotionPolicy.Resolve(Inputs(highContrastApplied: true, lever: UserMotionLever.Reduced)).Level);
        Assert.Equal(
            ResolvedMotionLevel.Off,
            MotionPolicy.Resolve(Inputs(systemAnimationsEnabled: false, lever: UserMotionLever.Reduced)).Level);
    }

    [Fact]
    public void Essential_source_ceiling_does_not_stack_and_does_not_raise()
    {
        // Two Reduced-shaped seats stay Reduced (a ceiling never deepens
        // anything), and the ceiling never lifts an Off.
        Assert.Equal(
            ResolvedMotionLevel.Reduced,
            MotionPolicy.Resolve(Inputs(triggers: PowerTriggersEx.BatterySaverOn, hardware: HardwareCeiling.EssentialSource)).Level);
        Assert.Equal(
            ResolvedMotionLevel.Off,
            MotionPolicy.Resolve(Inputs(highContrastApplied: true, hardware: HardwareCeiling.EssentialSource)).Level);
    }

    [Theory]
    [InlineData(PowerSaverModeEx.Auto, PowerTriggersEx.None, ResolvedMotionLevel.Full)]
    [InlineData(PowerSaverModeEx.Auto, PowerTriggersEx.BatterySaverOn, ResolvedMotionLevel.Reduced)]
    [InlineData(PowerSaverModeEx.Always, PowerTriggersEx.None, ResolvedMotionLevel.Reduced)]
    public void Pane_scale_motion_is_barred_at_every_resolved_level(
        PowerSaverModeEx powerMode, PowerTriggersEx triggers, ResolvedMotionLevel expectedLevel)
    {
        var resolved = MotionPolicy.Resolve(Inputs(
            powerMode: powerMode,
            triggers: triggers,
            isRemoteSession: true));

        Assert.Equal(expectedLevel, resolved.Level);
        Assert.False(resolved.PaneScaleMotionAllowed);
    }

    [Fact]
    public void Resolve_is_deterministic_for_equal_inputs()
    {
        // A pure function with no statics: equal inputs resolve to equal
        // results, twice in a row, in any order.
        var first = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Always,
            isRemoteSession: true,
            highContrastApplied: true,
            hardware: HardwareCeiling.EssentialSource,
            lever: UserMotionLever.Reduced));
        var second = MotionPolicy.Resolve(Inputs(
            powerMode: PowerSaverModeEx.Always,
            isRemoteSession: true,
            highContrastApplied: true,
            hardware: HardwareCeiling.EssentialSource,
            lever: UserMotionLever.Reduced));

        Assert.Equal(first, second);
    }
}
