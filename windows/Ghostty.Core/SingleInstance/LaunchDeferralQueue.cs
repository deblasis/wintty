using System;
using System.Collections.Generic;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// Holds forwarded launches that arrived before the app could open a window
/// for them, and hands them back once it can.
///
/// <para>It exists so a launch is never discarded. The secondary has already
/// written the request down the pipe and exited believing it was served, so
/// dropping one here is a window the user asked for that never appears, with
/// no error anywhere they can see. A launch that cannot be acted on yet waits
/// instead, and is replayed on the readiness edge.</para>
///
/// <para>Bounded by <see cref="Capacity"/>. The gap it bridges is the tail of
/// startup, so a queue deep enough to reach the cap is not a queue worth
/// growing: it is a sign the app is wedged. The OLDEST entry is the one
/// evicted, because the newest is the launch the user is looking at and the
/// oldest is the one most likely superseded by it.</para>
///
/// <para>Not thread-safe, and not meant to be. Both ends run on the UI
/// thread: requests arrive through the single-instance callback's
/// <c>TryEnqueue</c>, and <see cref="MarkReady"/> is called from
/// <c>OnLaunched</c>. A lock would add nothing the dispatcher does not
/// already guarantee.</para>
/// </summary>
public sealed class LaunchDeferralQueue
{
    /// <summary>
    /// How many launches will wait. Sized for a user mashing a launcher while
    /// the first window is still coming up, not for a backlog: eight queued
    /// launches inside a sub-second gap is a wedged app, and the ninth is
    /// honestly better reported than silently queued behind it.
    /// </summary>
    public const int Capacity = 8;

    private readonly List<LaunchRequest> _pending = new();
    private bool _ready;

    /// <summary>How many launches are waiting.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Whether <see cref="MarkReady"/> has run. Once true, nothing is ever
    /// held again: the app is past the point where it could not act, so a
    /// launch reaching this queue afterwards is one the app cannot serve and
    /// the caller has to report the loss.
    /// </summary>
    public bool IsReady => _ready;

    /// <summary>Hold a launch for the readiness edge. Reports nothing about
    /// the launch it may have evicted to make room; see the overload that
    /// does. Returns false, holding nothing, once <see cref="MarkReady"/> has
    /// run.</summary>
    public bool Defer(LaunchRequest request) => Defer(request, out _);

    /// <summary>
    /// Hold a launch for the readiness edge. Returns false, holding nothing,
    /// once <see cref="MarkReady"/> has run.
    /// </summary>
    /// <param name="request">The launch to hold.</param>
    /// <param name="evicted">The launch dropped to make room, when
    /// <see cref="Capacity"/> was already reached, otherwise null. Null
    /// whenever the return is false, which is the one outcome that touches
    /// nothing already held.</param>
    /// <remarks>
    /// Always makes room rather than refusing. From the caller's side the
    /// question is only "will this one be replayed", so the return stays a
    /// bool and the eviction travels separately as the thing that was lost.
    /// </remarks>
    public bool Defer(LaunchRequest request, out LaunchRequest? evicted)
    {
        ArgumentNullException.ThrowIfNull(request);

        evicted = null;
        if (_ready) return false;

        if (_pending.Count >= Capacity)
        {
            evicted = _pending[0];
            _pending.RemoveAt(0);
        }

        _pending.Add(request);
        return true;
    }

    /// <summary>
    /// Latch readiness and return everything held, oldest first. Idempotent:
    /// a second call returns an empty list.
    /// </summary>
    /// <remarks>
    /// The latch is a point of no return rather than a flag the caller could
    /// unset. A request handed back and then deferred again by a re-entrant
    /// call would land in a queue nobody drains, and returning it to a caller
    /// that is itself mid-teardown is exactly the loss this type exists to
    /// make loud instead of silent.
    /// </remarks>
    public IReadOnlyList<LaunchRequest> MarkReady()
    {
        _ready = true;

        if (_pending.Count == 0) return Array.Empty<LaunchRequest>();
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }
}
