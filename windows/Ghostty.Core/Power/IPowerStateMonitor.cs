using System;

namespace Ghostty.Core.Power;

/// <summary>
/// App-scoped singleton that reports whether low-power mode is currently
/// active and why. Render-path consumers poll <see cref="IsLowPowerActive"/>
/// when they paint or resolve an effect; consumers that set state once
/// (e.g. a title-bar icon) subscribe to <see cref="LowPowerChanged"/>.
/// </summary>
public interface IPowerStateMonitor
{
    /// <summary>True when low-power mode is currently active.</summary>
    bool IsLowPowerActive { get; }

    /// <summary>The OS signals that are currently contributing to the active state.
    /// When <see cref="IsLowPowerActive"/> is false, this is <see cref="PowerSaverTrigger.None"/>.</summary>
    PowerSaverTrigger ActiveTriggers { get; }

    /// <summary>Raised on the monitor's synchronization context when
    /// <see cref="IsLowPowerActive"/> flips value. Not raised when only
    /// <see cref="ActiveTriggers"/> changes without flipping active.</summary>
    event EventHandler LowPowerChanged;

    /// <summary>Begin observing signals. Idempotent.</summary>
    void Start();

    /// <summary>Stop observing signals and release subscriptions. Idempotent.</summary>
    void Stop();
}
