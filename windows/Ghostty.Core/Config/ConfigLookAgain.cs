using System;

namespace Ghostty.Core.Config;

/// <summary>
/// The config service's own "look again": one reload, scheduled after a
/// delay, for when a declined reload needs another look and the file
/// watcher cannot give it one.
/// </summary>
/// <remarks>
/// <para>Every question a declined reload asks (a file that vanished, a
/// layered file missing from a count, a file that would not open) is
/// answered by a LATER load. The watcher's <c>Resettle</c> schedules that
/// load when there is a watcher. With <c>auto-reload-config</c> off, the
/// default, there is none, and a question that waited on it waited for the
/// life of the process: wintty#1155 for the vanished file, and the same
/// lockout on the layered-file shrink, where the budget only counted asks
/// the watcher took. This is the look those questions get instead.</para>
///
/// <para>One timer for all of them. A decline asks one question at a time,
/// and re-arming replaces a pending fire, so a burst of declines lands on
/// one reload rather than a train of them.</para>
///
/// <para>The delay is clamped to <c>[0, maxDelay]</c>. Every caller's
/// delay is at most that already, and the clamp is what makes it a fact
/// rather than an assumption: a delay computed from a stepped clock can
/// neither throw inside the timer (whose limit is about 49.7 days) nor
/// leave the question waiting for longer than one floor.</para>
/// </remarks>
public sealed class ConfigLookAgain : IDisposable
{
    /// <summary>
    /// The delay a declined reload asks for when it has no floor of its own
    /// to wait out: the watcher's debounce, so a host with no watcher asks
    /// at the same cadence and on the same budgets as one with a watcher.
    /// </summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<bool> _watcherAsk;
    private readonly ISchedulerTimer _timer;
    private readonly TimeSpan _maxDelay;
    private bool _stopped;

    /// <param name="watcherAsk">The watcher's own ask, its <c>Resettle</c>:
    /// true when it scheduled a delivery. Read at each ask, so a host whose
    /// watcher is created later, or never, passes a lambda over the
    /// field.</param>
    /// <param name="timer">One-shot timer; its callback is set here.</param>
    /// <param name="onLook">What a look does when it fires. Runs on the
    /// timer's thread, so the host marshals it to wherever it reloads.</param>
    /// <param name="maxDelay">The longest delay a look may be scheduled
    /// for. Positive.</param>
    public ConfigLookAgain(
        Func<bool> watcherAsk,
        ISchedulerTimer timer,
        Action onLook,
        TimeSpan maxDelay)
    {
        ArgumentNullException.ThrowIfNull(watcherAsk);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(onLook);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDelay, TimeSpan.Zero);

        _watcherAsk = watcherAsk;
        _timer = timer;
        _maxDelay = maxDelay;
        _timer.Callback = onLook;
    }

    /// <summary>
    /// A declined reload's ask for one more look: the watcher's delivery when
    /// it takes the ask, and otherwise one look <see cref="SettleDelay"/>
    /// from now. Answers whether either was scheduled, which is what a
    /// declined reload's ask budgets count.
    /// </summary>
    /// <remarks>
    /// The fallback is the whole difference with no watcher. The watcher's
    /// ask used to be the only one, so a budget counting asks somebody took
    /// never moved: a layered-file shrink was never confirmed and a user who
    /// deleted one of two layered config files had every reload refused until
    /// restart. With it, both budgets run at the watcher's own cadence.
    /// </remarks>
    public bool Ask() => _watcherAsk() || Schedule(SettleDelay);

    /// <summary>
    /// Arm one look <paramref name="delay"/> from now, clamped, and answer
    /// whether it was armed. False only once stopped.
    /// </summary>
    public bool Schedule(TimeSpan delay)
    {
        if (_stopped) return false;

        var clamped = delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay > _maxDelay ? _maxDelay
            : delay;
        _timer.Schedule(clamped);
        return true;
    }

    /// <summary>
    /// No further looks: a pending one is cancelled and later asks are
    /// refused. For teardown, where a look that fired after the app was
    /// freed would push a config into it (issue #208).
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        _timer.Cancel();
    }

    public void Dispose()
    {
        _stopped = true;
        _timer.Dispose();
    }
}
