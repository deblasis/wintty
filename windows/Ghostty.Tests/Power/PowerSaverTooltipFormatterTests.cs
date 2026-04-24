using Ghostty.Core.Power;
using Xunit;

namespace Ghostty.Tests.Power;

public class PowerSaverTooltipFormatterTests
{
    [Fact]
    public void Empty_trigger_set_returns_generic_text()
    {
        // Covers Always mode, where the user forced low-power without an OS signal.
        var text = PowerSaverTooltipFormatter.Format(PowerSaverTrigger.None);

        Assert.Equal("Power saving mode active.", text);
    }

    [Fact]
    public void Single_trigger_renders_as_full_sentence()
    {
        var text = PowerSaverTooltipFormatter.Format(PowerSaverTrigger.BatterySaverOn);

        Assert.Equal("Power saving: Battery Saver is on.", text);
    }

    [Fact]
    public void Multiple_triggers_render_as_comma_and_period()
    {
        var text = PowerSaverTooltipFormatter.Format(
            PowerSaverTrigger.BatterySaverOn
          | PowerSaverTrigger.OnBattery
          | PowerSaverTrigger.TransparencyEffectsOff);

        Assert.Equal(
            "Power saving: Battery Saver is on, running on battery, transparency effects are off.",
            text);
    }

    [Fact]
    public void Remote_session_alone_has_its_own_phrasing()
    {
        var text = PowerSaverTooltipFormatter.Format(PowerSaverTrigger.RemoteSession);

        Assert.Equal("Power saving: Remote Desktop session detected.", text);
    }
}
