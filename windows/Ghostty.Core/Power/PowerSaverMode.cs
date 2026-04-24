namespace Ghostty.Core.Power;

/// <summary>
/// User-facing power-saver policy mode, read from the
/// <c>power-saver-mode</c> config key.
/// </summary>
public enum PowerSaverMode
{
    /// <summary>React to the OS signals (Battery Saver, on-battery, transparency-off, RDP).</summary>
    Auto,

    /// <summary>Always report low-power active, regardless of OS signals.</summary>
    Always,

    /// <summary>Never report low-power active, even when OS signals say so.</summary>
    Never,
}
