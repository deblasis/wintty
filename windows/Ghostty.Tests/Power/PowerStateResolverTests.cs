using Ghostty.Core.Power;
using Xunit;

namespace Ghostty.Tests.Power;

public class PowerStateResolverTests
{
    [Fact]
    public void Never_mode_reports_inactive_regardless_of_signals()
    {
        var (active, triggers) = PowerStateResolver.Resolve(
            mode: PowerSaverMode.Never,
            batterySaverOn: true,
            onBattery: true,
            transparencyEffectsOff: true,
            remoteSession: true);

        Assert.False(active);
        Assert.Equal(PowerSaverTrigger.None, triggers);
    }

    [Fact]
    public void Always_mode_reports_active_regardless_of_signals()
    {
        var (active, triggers) = PowerStateResolver.Resolve(
            mode: PowerSaverMode.Always,
            batterySaverOn: false,
            onBattery: false,
            transparencyEffectsOff: false,
            remoteSession: false);

        Assert.True(active);
        // Always mode has no backing triggers to report.
        Assert.Equal(PowerSaverTrigger.None, triggers);
    }

    [Fact]
    public void Auto_mode_inactive_when_no_signals_fire()
    {
        var (active, triggers) = PowerStateResolver.Resolve(
            mode: PowerSaverMode.Auto,
            batterySaverOn: false,
            onBattery: false,
            transparencyEffectsOff: false,
            remoteSession: false);

        Assert.False(active);
        Assert.Equal(PowerSaverTrigger.None, triggers);
    }

    [Theory]
    [InlineData(true,  false, false, false, PowerSaverTrigger.BatterySaverOn)]
    [InlineData(false, true,  false, false, PowerSaverTrigger.OnBattery)]
    [InlineData(false, false, true,  false, PowerSaverTrigger.TransparencyEffectsOff)]
    [InlineData(false, false, false, true,  PowerSaverTrigger.RemoteSession)]
    public void Auto_mode_active_when_any_single_signal_fires(
        bool bs, bool ob, bool te, bool rdp, PowerSaverTrigger expected)
    {
        var (active, triggers) = PowerStateResolver.Resolve(
            mode: PowerSaverMode.Auto,
            batterySaverOn: bs,
            onBattery: ob,
            transparencyEffectsOff: te,
            remoteSession: rdp);

        Assert.True(active);
        Assert.Equal(expected, triggers);
    }

    [Fact]
    public void Auto_mode_combines_multiple_active_signals_into_flag_set()
    {
        var (active, triggers) = PowerStateResolver.Resolve(
            mode: PowerSaverMode.Auto,
            batterySaverOn: true,
            onBattery: true,
            transparencyEffectsOff: false,
            remoteSession: true);

        Assert.True(active);
        Assert.Equal(
            PowerSaverTrigger.BatterySaverOn
          | PowerSaverTrigger.OnBattery
          | PowerSaverTrigger.RemoteSession,
            triggers);
    }
}
