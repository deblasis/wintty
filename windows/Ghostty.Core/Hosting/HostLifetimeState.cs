using System;
using System.Collections.Generic;

namespace Ghostty.Core.Hosting;

/// <summary>
/// Ownership-and-lifetime state for a single Ghostty host instance.
/// Carries the answers to "do I free the app on Dispose" and "have I
/// been disposed yet" in a pointer-free form so the invariants can be
/// unit-tested without touching libghostty.
/// </summary>
internal sealed class HostLifetimeState
{
    public bool IsBootstrap { get; }
    public bool OwnsApp => IsBootstrap;
    public bool IsDisposed { get; private set; }

    private HostLifetimeState(bool isBootstrap)
    {
        IsBootstrap = isBootstrap;
    }

    public static HostLifetimeState Bootstrap() => new(isBootstrap: true);
    public static HostLifetimeState PerWindow() => new(isBootstrap: false);

    /// <summary>Idempotent. Marks the host as disposed.</summary>
    public void MarkDisposed() => IsDisposed = true;
}

/// <summary>
/// Process-level supervisor that enforces the drain-last invariant:
/// every per-window host must dispose before the bootstrap host.
/// Injected into <c>GhosttyHost</c> via <see cref="IAppHandleOwnership"/>
/// so the real dispose path consults this supervisor and the tests
/// can exercise the transitions against a fake.
///
/// Thread safety: UI-thread-only. All NotifyDisposed calls must
/// originate from the UI dispatcher. GhosttyHost is sealed and has no
/// finalizer, so disposal from a finalizer thread is not a concern.
/// </summary>
internal sealed class HostLifetimeSupervisor
{
    private readonly HashSet<HostLifetimeState> _live = new();
    private HostLifetimeState? _bootstrap;

    public int LivePerWindowCount
    {
        get
        {
            int n = 0;
            foreach (var s in _live)
                if (!s.IsBootstrap && !s.IsDisposed) n++;
            return n;
        }
    }

    public HostLifetimeState RegisterBootstrap()
    {
        if (_bootstrap is not null)
            throw new InvalidOperationException(
                "HostLifetimeSupervisor: bootstrap already registered.");
        _bootstrap = HostLifetimeState.Bootstrap();
        _live.Add(_bootstrap);
        return _bootstrap;
    }

    public HostLifetimeState RegisterPerWindow()
    {
        var s = HostLifetimeState.PerWindow();
        _live.Add(s);
        return s;
    }

    /// <summary>
    /// Called from the host's Dispose path. Enforces the drain-last
    /// invariant. Throws if a per-window host tries to Dispose after
    /// the bootstrap, or if the bootstrap tries to Dispose while any
    /// per-window host is still live.
    /// </summary>
    public void NotifyDisposed(HostLifetimeState state)
    {
        if (state.IsBootstrap)
        {
            if (LivePerWindowCount > 0)
                throw new InvalidOperationException(
                    "HostLifetimeSupervisor: bootstrap cannot dispose while per-window hosts are live. Drain per-window hosts first.");
            _live.Remove(state);
            return;
        }

        if (_bootstrap is not null && _bootstrap.IsDisposed)
            throw new InvalidOperationException(
                "HostLifetimeSupervisor: per-window host cannot dispose after bootstrap has been freed.");
        _live.Remove(state);
    }
}

/// <summary>
/// Abstraction consulted by <c>GhosttyHost.Dispose</c> to decide
/// whether to call <c>AppFree</c> and to notify the supervisor that
/// this host has been disposed. The production implementation is
/// <see cref="SupervisedOwnership"/>, which wraps a
/// <see cref="HostLifetimeState"/> and a
/// <see cref="HostLifetimeSupervisor"/>; tests swap in a fake that
/// records calls without a real supervisor.
/// </summary>
internal interface IAppHandleOwnership
{
    HostLifetimeState State { get; }
    void NotifyDisposed();
}

/// <summary>
/// Production <see cref="IAppHandleOwnership"/> backed by a shared
/// <see cref="HostLifetimeSupervisor"/>. The ctor takes the state
/// returned by the supervisor's register call and holds on to the
/// supervisor so <see cref="NotifyDisposed"/> can enforce the
/// drain-last invariant.
/// </summary>
internal sealed class SupervisedOwnership : IAppHandleOwnership
{
    private readonly HostLifetimeSupervisor _supervisor;
    public HostLifetimeState State { get; }

    public SupervisedOwnership(HostLifetimeState state, HostLifetimeSupervisor supervisor)
    {
        State = state;
        _supervisor = supervisor;
    }

    public void NotifyDisposed() => _supervisor.NotifyDisposed(State);
}
