using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Power;

/// <summary>
/// Renders a <see cref="PowerSaverTrigger"/> flag set as a human-readable
/// tooltip string for the title-bar indicator.
/// </summary>
public static class PowerSaverTooltipFormatter
{
    public static string Format(PowerSaverTrigger triggers)
    {
        if (triggers == PowerSaverTrigger.None)
        {
            return "Power saving mode active.";
        }

        var parts = new List<string>(4);
        if (triggers.HasFlag(PowerSaverTrigger.BatterySaverOn))
            parts.Add("Battery Saver is on");
        if (triggers.HasFlag(PowerSaverTrigger.OnBattery))
            parts.Add("running on battery");
        if (triggers.HasFlag(PowerSaverTrigger.TransparencyEffectsOff))
            parts.Add("transparency effects are off");
        if (triggers.HasFlag(PowerSaverTrigger.RemoteSession))
            parts.Add("Remote Desktop session detected");

        var sb = new StringBuilder("Power saving: ");
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(parts[i]);
        }
        sb.Append('.');
        return sb.ToString();
    }
}
