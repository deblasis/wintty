using System;
using Ghostty.Core.Config;

namespace Ghostty.Core.Panes;

/// <summary>
/// Pure lifecycle for one pane's startup glow. Renders nothing; raises
/// <see cref="StateChanged"/> so a WinUI renderer can react. The glow ends
/// when the cap timer elapses. A single <see cref="ISchedulerTimer"/> is
/// reused for both the cap and the fade window, so the whole lifecycle is
/// testable with a fake timer.
///
/// Thread-safety: state transitions are guarded by an internal lock so the
/// injected timer's callback (which may run on a threadpool thread) and the
/// owner's calls (Start/Close/Dispose) cannot corrupt state.
/// <see cref="StateChanged"/> is raised outside the lock; handlers that touch
/// UI must still marshal onto the dispatcher.
/// </summary>
public sealed class PaneStartupGlowState : IDisposable
{
    public enum Phase { Idle, Glowing, FadingOut }

    private readonly object _gate = new();
    private readonly ISchedulerTimer _timer;
    private readonly TimeSpan _cap;
    private readonly TimeSpan _fade;

    public Phase Current { get; private set; } = Phase.Idle;

    /// <summary>Raised on every phase change. May fire on a threadpool thread
    /// (the production timer callback is not UI-thread); subscribers that touch
    /// UI must marshal onto the dispatcher.</summary>
    public event Action<Phase>? StateChanged;

    public PaneStartupGlowState(ISchedulerTimer timer, TimeSpan cap, TimeSpan fade)
    {
        ArgumentNullException.ThrowIfNull(timer);
        _timer = timer;
        _cap = cap;
        _fade = fade;
        _timer.Callback = OnTimerFired;
    }

    /// <summary>Begin glowing. No-op if already past Idle. The caller is
    /// responsible for deciding whether to start (enablement, pane size).</summary>
    public void Start()
    {
        Phase? changed = null;
        lock (_gate)
        {
            if (Current != Phase.Idle) return;
            Current = Phase.Glowing;
            changed = Phase.Glowing;
            _timer.Schedule(_cap);
        }
        Raise(changed);
    }

    /// <summary>The surface produced its first render: end the glow early.
    /// No-op unless currently <see cref="Phase.Glowing"/> (Idle/FadingOut
    /// ignore it). The fade timer supersedes the pending cap
    /// (last-schedule-wins), so the cap is the fallback only for surfaces
    /// that never render.</summary>
    public void NotifyReady()
    {
        Phase? changed = null;
        lock (_gate)
        {
            if (Current == Phase.Glowing) changed = BeginFadeLocked();
        }
        Raise(changed);
    }

    /// <summary>Pane closed: cancel any pending timer and return to Idle.</summary>
    public void Close()
    {
        Phase? changed = null;
        lock (_gate)
        {
            _timer.Cancel();
            if (Current != Phase.Idle)
            {
                Current = Phase.Idle;
                changed = Phase.Idle;
            }
        }
        Raise(changed);
    }

    private void OnTimerFired()
    {
        Phase? changed = null;
        lock (_gate)
        {
            switch (Current)
            {
                case Phase.Glowing: changed = BeginFadeLocked(); break;   // cap reached
                case Phase.FadingOut:                                     // fade done
                    Current = Phase.Idle;
                    changed = Phase.Idle;
                    break;
            }
        }
        Raise(changed);
    }

    // Caller must hold _gate.
    private Phase BeginFadeLocked()
    {
        Current = Phase.FadingOut;
        _timer.Schedule(_fade);
        return Phase.FadingOut;
    }

    private void Raise(Phase? changed)
    {
        if (changed is { } p) StateChanged?.Invoke(p);
    }

    public void Dispose()
    {
        // Cancel under the lock, but dispose the timer OUTSIDE it: the real
        // timer's Dispose waits for an in-flight callback to finish, and that
        // callback takes _gate. Disposing under the lock would deadlock.
        lock (_gate) { _timer.Cancel(); }
        _timer.Dispose();
    }
}
