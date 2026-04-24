using System;
using Ghostty.Core.Power;

namespace Ghostty.Tests.Power;

/// <summary>
/// In-memory test double for <see cref="IPowerStateMonitor"/>. Tests set
/// <see cref="IsLowPowerActive"/> / <see cref="ActiveTriggers"/> directly
/// and call <see cref="Flip"/> to raise <see cref="LowPowerChanged"/>.
/// </summary>
internal sealed class FakePowerStateMonitor : IPowerStateMonitor
{
    public bool IsLowPowerActive { get; set; }
    public PowerSaverTrigger ActiveTriggers { get; set; }
    public event EventHandler? LowPowerChanged;

    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }

    public void Start() => StartCalls++;
    public void Stop()  => StopCalls++;

    public void Flip(bool active, PowerSaverTrigger triggers)
    {
        IsLowPowerActive = active;
        ActiveTriggers   = triggers;
        LowPowerChanged?.Invoke(this, EventArgs.Empty);
    }
}
