using System;

namespace Ghostty.Core.Activation;

/// <summary>
/// Carries a protocol activation (a <c>wintty://</c> URI) from wherever it
/// lands to whoever can act on it.
///
/// The awkward part it exists to solve is the same one
/// <see cref="ToastActivationRelay"/> solves for toast clicks: the consumer
/// of a <c>wintty://</c> URI (a downstream build tier's router) does not
/// exist yet when a cold launch's activation is probed, and a launch
/// forwarded by a secondary arrives after startup entirely, through the
/// single-instance pipe -- where the probe never ran, because it ran in the
/// secondary that exited. So a URI nobody is listening for is LATCHED and
/// handed to the first subscriber instead of being raised into an empty
/// invocation list and lost.
///
/// Deliberately warm-path only. The cold start keeps the probe-fed local in
/// <c>App.OnLaunched</c>, which the downstream router already consumes
/// directly; noting the cold URI here too would deliver it twice once a
/// subscriber exists -- once from this latch, once from the local.
///
/// Thread-safe, on the same terms as <see cref="ToastActivationRelay"/>:
/// handlers run with no lock held, so a handler may call back into the
/// relay, and a handler must do its own marshalling to whatever thread it
/// needs.
/// </summary>
internal sealed class ProtocolActivationRelay
{
    private readonly object _gate = new();
    private readonly Action<Exception>? _onHandlerFailed;
    private Uri? _pending;
    private Action<Uri>? _handlers;

    /// <param name="onHandlerFailed">
    /// Where a throw from a handler goes. A replayed activation runs the
    /// handler inline on the subscriber's own thread, which during startup is
    /// the startup path: without this, a subscriber that throws turns a
    /// forwarded deep link into a failed launch (the secondary is told the
    /// launch faulted and opens its own window instead).
    /// </param>
    public ProtocolActivationRelay(Action<Exception>? onHandlerFailed = null)
        => _onHandlerFailed = onHandlerFailed;

    /// <summary>
    /// The activation waiting for its first subscriber, or null. A read, not
    /// a consume: a process that forwards its launch elsewhere and exits
    /// never subscribes, so the latch is the only record of its URI.
    /// </summary>
    public Uri? Pending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>
    /// Add a handler, and hand it any latched activation immediately. The
    /// first subscriber after a latch consumes it; see the type remarks for
    /// why the latch exists.
    /// </summary>
    public void Subscribe(Action<Uri> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        Uri? replay;
        lock (_gate)
        {
            _handlers += handler;
            replay = _pending;
            _pending = null;
        }

        if (replay is { } pending) Invoke(handler, pending);
    }

    public void Unsubscribe(Action<Uri> handler)
    {
        lock (_gate) _handlers -= handler;
    }

    /// <summary>
    /// Record a URI. Fans out when anyone is listening; latches otherwise.
    /// </summary>
    public void Note(Uri uri)
    {
        Action<Uri>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
            if (handlers is null)
            {
                _pending = uri;
                return;
            }
        }

        // Outside the lock deliberately: a handler can re-enter the relay,
        // and holding the gate across it would let one slow handler block
        // every other thread reading Pending.
        //
        // Target by target rather than invoking the multicast delegate whole.
        // A single call propagates the first exception immediately and never
        // reaches the targets behind it, so one throwing subscriber would
        // silently cost every later one its URI.
        foreach (var target in handlers.GetInvocationList())
            Invoke((Action<Uri>)target, uri);
    }

    /// <summary>
    /// Drop every handler and any latched activation. For teardown: the relay
    /// outlives the objects that subscribe to it, so leaving handlers
    /// attached roots them for the life of the process.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _handlers = null;
            _pending = null;
        }
    }

    private void Invoke(Action<Uri> handler, Uri uri)
    {
        try
        {
            handler(uri);
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
