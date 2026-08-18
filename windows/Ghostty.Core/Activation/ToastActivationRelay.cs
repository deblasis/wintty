using System;

namespace Ghostty.Core.Activation;

/// <summary>
/// Carries a toast click from wherever it lands to whoever can act on it.
///
/// The awkward part it exists to solve is timing. A cold launch delivers the
/// click during shell registration, at the very top of startup, long before
/// the windows and services that could act on it are constructed. So an
/// activation that arrives with nobody listening is LATCHED and handed to the
/// next subscriber instead of being raised into an empty invocation list and
/// lost.
///
/// The latch is consumed by the FIRST subscriber to arrive after it, and only
/// that one. Subscription order is therefore load-bearing: a subscriber wired
/// earlier than the one that can actually act on a click will swallow every
/// cold-launch activation. Anyone adding a second subscriber must wire it
/// AFTER the one that focuses windows, or accept that it sees only clicks that
/// arrive while the app is already running.
///
/// Thread-safe. <see cref="Note"/> is reached from a WinRT COM callback
/// thread; handlers are invoked with no lock held, so a handler may call back
/// into the relay and a slow handler cannot block another thread's
/// <see cref="Pending"/> read. Handlers must therefore do their own marshalling
/// to a UI thread.
/// </summary>
internal sealed class ToastActivationRelay
{
    private readonly object _gate = new();
    private readonly Action<Exception>? _onHandlerFailed;
    private ToastActivation? _pending;
    private Action<ToastActivation>? _handlers;

    /// <param name="onHandlerFailed">
    /// Where a throw from a handler goes. A replayed activation runs the
    /// handler inline on the subscriber's own thread, which on a cold launch
    /// is the startup path: without this, a future subscriber that throws
    /// turns a toast-driven launch into a silent launch failure.
    /// </param>
    public ToastActivationRelay(Action<Exception>? onHandlerFailed = null)
        => _onHandlerFailed = onHandlerFailed;

    /// <summary>
    /// The activation waiting for its first subscriber, or
    /// <see cref="ToastActivation.None"/>. A read, not a consume: a process
    /// that forwards its launch elsewhere and exits never subscribes, so the
    /// latch is the only record of its click.
    /// </summary>
    public ToastActivation Pending
    {
        get { lock (_gate) return _pending ?? ToastActivation.None; }
    }

    /// <summary>
    /// Add a handler, and hand it any latched activation immediately. See the
    /// type remarks on why order matters.
    /// </summary>
    public void Subscribe(Action<ToastActivation> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        ToastActivation? replay;
        lock (_gate)
        {
            _handlers += handler;
            replay = _pending;
            _pending = null;
        }

        if (replay is { } pending) Invoke(handler, pending);
    }

    public void Unsubscribe(Action<ToastActivation> handler)
    {
        lock (_gate) _handlers -= handler;
    }

    /// <summary>
    /// Record a click. Fans out when anyone is listening; latches otherwise.
    /// </summary>
    public void Note(ToastActivation activation)
    {
        Action<ToastActivation>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
            if (handlers is null)
            {
                _pending = activation;
                return;
            }
        }

        // Outside the lock deliberately: a handler activates windows and can
        // re-enter the relay, and holding the gate across it would let one
        // slow handler block every other thread reading Pending.
        Invoke(handlers, activation);
    }

    /// <summary>
    /// Drop every handler and any latched activation. For teardown: the relay
    /// outlives the objects that subscribe to it, so leaving handlers attached
    /// roots them for the life of the process.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _handlers = null;
            _pending = null;
        }
    }

    private void Invoke(Action<ToastActivation> handler, ToastActivation activation)
    {
        try
        {
            handler(activation);
        }
        catch (Exception ex)
        {
            if (_onHandlerFailed is null) throw;
            _onHandlerFailed(ex);
        }
    }
}
