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
    private bool _launchActivationNoted;

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
        //
        // Target by target rather than invoking the multicast delegate whole.
        // A single call propagates the first exception immediately and never
        // reaches the targets behind it, so one throwing subscriber would
        // silently cost every later one its click.
        foreach (var target in handlers.GetInvocationList())
            Invoke((Action<ToastActivation>)target, activation);
    }

    /// <summary>
    /// Record the activation this PROCESS WAS LAUNCHED FOR, at most once.
    /// Returns false when it was already recorded and nothing was done.
    ///
    /// The launch click can reach the app twice: once off the activation
    /// arguments the shell hands the process, and once through the
    /// notification callback, describing the same click. Which arrives first
    /// is not documented, so both callers come through here and whichever gets
    /// here first wins. Without it the surface is focused twice for one click
    /// -- a redundant activation, focus churn, and for the quick terminal a
    /// second run through its reveal animation.
    /// </summary>
    public bool TryNoteLaunchActivation(ToastActivation activation)
    {
        lock (_gate)
        {
            if (_launchActivationNoted) return false;
            _launchActivationNoted = true;
        }

        Note(activation);
        return true;
    }

    /// <summary>
    /// Drop every handler, any latched activation, and the launch-activation
    /// record. For teardown: the relay outlives the objects that subscribe to
    /// it, so leaving handlers attached roots them for the life of the process.
    ///
    /// Does NOT stop a fan-out already in flight: <see cref="Note"/> snapshots
    /// the handlers before it starts invoking them, deliberately, so that a
    /// handler which unsubscribes cannot mutate the list being walked.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _handlers = null;
            _pending = null;
            _launchActivationNoted = false;
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

            // The failure sink is caller-supplied and reached from inside a
            // catch: a throw from it would escape Note or Subscribe, which is
            // the failure this whole guard exists to prevent. There is nowhere
            // left to report to, so it is dropped.
            try { _onHandlerFailed(ex); }
            catch { /* reporting a failure must not become one */ }
        }
    }
}
