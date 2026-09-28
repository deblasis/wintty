using Ghostty.Core.Motion;
using Ghostty.Core.Power;

namespace Ghostty.Power;

/// <summary>
/// Maps the power monitor's real enums onto the truth table's mirrors.
/// The mirrors are hand-kept copies (the policy stays plain, the real
/// enums carry the config and monitor vocabulary), and the Ex parity
/// pins hold the pairs apart by name precisely so this adapter can map
/// BY NAME: the trigger bits are deliberately swapped between the sides,
/// so a positional cast would re-mean the composite silently.
/// </summary>
internal static class PowerPolicyAdapter
{
    public static PowerSaverModeEx Mode(this PowerSaverMode mode) => mode switch
    {
        PowerSaverMode.Always => PowerSaverModeEx.Always,
        PowerSaverMode.Never => PowerSaverModeEx.Never,
        _ => PowerSaverModeEx.Auto,
    };

    /// <summary>
    /// The trigger composite, by name, remote-session bit included. The
    /// composite goes over raw: the truth table masks the transport bit
    /// out of its level test itself, and the adapter does not pre-empt
    /// that contract.
    /// </summary>
    public static PowerTriggersEx Triggers(this PowerSaverTrigger triggers)
    {
        var mapped = PowerTriggersEx.None;
        if (triggers.HasFlag(PowerSaverTrigger.BatterySaverOn))
            mapped |= PowerTriggersEx.BatterySaverOn;
        if (triggers.HasFlag(PowerSaverTrigger.OnBattery))
            mapped |= PowerTriggersEx.OnBattery;
        if (triggers.HasFlag(PowerSaverTrigger.TransparencyEffectsOff))
            mapped |= PowerTriggersEx.TransparencyEffectsOff;
        if (triggers.HasFlag(PowerSaverTrigger.RemoteSession))
            mapped |= PowerTriggersEx.RemoteSession;
        return mapped;
    }
}
