namespace Ghostty.Core.Power;

/// <summary>
/// Pure-logic resolver: given the user's configured <see cref="PowerSaverMode"/>
/// and the current OS signals, decide whether low-power mode is active
/// and which triggers contributed.
/// </summary>
public static class PowerStateResolver
{
    public static (bool IsActive, PowerSaverTrigger Triggers) Resolve(
        PowerSaverMode mode,
        bool batterySaverOn,
        bool onBattery,
        bool transparencyEffectsOff,
        bool remoteSession)
    {
        switch (mode)
        {
            case PowerSaverMode.Never:
                return (false, PowerSaverTrigger.None);

            case PowerSaverMode.Always:
                // Always mode has no backing OS triggers - the user forced it.
                return (true, PowerSaverTrigger.None);

            case PowerSaverMode.Auto:
            default:
                var triggers = PowerSaverTrigger.None;
                if (batterySaverOn)         triggers |= PowerSaverTrigger.BatterySaverOn;
                if (onBattery)              triggers |= PowerSaverTrigger.OnBattery;
                if (transparencyEffectsOff) triggers |= PowerSaverTrigger.TransparencyEffectsOff;
                if (remoteSession)          triggers |= PowerSaverTrigger.RemoteSession;
                return (triggers != PowerSaverTrigger.None, triggers);
        }
    }
}
