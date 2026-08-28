using System;
using Ghostty.Core.Config;

namespace Ghostty.Core.Panes;

/// <summary>STUB: no lifecycle yet, so the tests can only fail.</summary>
public sealed class PaneStartupGlowState : IDisposable
{
    public enum Phase { Idle, Glowing, FadingOut }

    public PaneStartupGlowState(ISchedulerTimer timer, TimeSpan cap, TimeSpan fade)
    {
        ArgumentNullException.ThrowIfNull(timer);
    }

    public Phase Current => Phase.Idle;

#pragma warning disable CS0067
    public event Action<Phase>? StateChanged;
#pragma warning restore CS0067

    public void Start() { }
    public void NotifyReady() { }
    public void Close() { }
    public void Dispose() { }
}
