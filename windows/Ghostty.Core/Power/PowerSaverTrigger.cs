using System;

namespace Ghostty.Core.Power;

/// <summary>
/// The individual OS signals that can contribute to low-power mode being
/// active. Exposed as a flag set so consumers (e.g. the title-bar
/// tooltip) can describe why the mode is on.
/// </summary>
[Flags]
public enum PowerSaverTrigger
{
    None                   = 0,
    BatterySaverOn         = 1 << 0,
    OnBattery              = 1 << 1,
    TransparencyEffectsOff = 1 << 2,
    RemoteSession          = 1 << 3,
}
