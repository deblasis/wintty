using System;
using System.Diagnostics;

namespace Ghostty.Core.Config;

/// <summary>How a watcher event reads to <see cref="ConfigFilePresence"/>.
/// The watcher classifies the file system's own reports once, where the
/// event carries its kind, and every consumer downstream reads only
/// these.</summary>
public enum ConfigFileEventKind
{
    /// <summary>The file's content or metadata changed in place. Also the
    /// stand-in for any event that is not unambiguously a delete or an
    /// appearance.</summary>
    Changed,

    /// <summary>The file was deleted, or renamed away from the watched
    /// name (a <c>ReplaceFile</c> or backup-rename save's first half
    /// reports as this).</summary>
    Deleted,

    /// <summary>The file was created, or renamed onto the watched name:
    /// an atomic save's completing half.</summary>
    Appeared,
}

/// <summary>Where <see cref="ConfigFilePresence"/> stands.</summary>
public enum ConfigFilePresenceState
{
    /// <summary>The file is there, or nothing seen says otherwise.</summary>
    Present,

    /// <summary>A delete event was seen and the file has not come back
    /// yet. A save in flight and a deletion look identical here, and the
    /// difference is resolved by what arrives next, not by a timer that
    /// guesses.</summary>
    PendingDelete,

    /// <summary>The delete outlived the backstop with no reappearance:
    /// the file is gone for good, and the session keeps the settings it
    /// had.</summary>
    Gone,
}

/// <summary>What one event or one backstop poll did to the state. A change
/// that did not move the state is <see cref="ConfigFilePresenceChange.StateChanged"/>
/// false with the state it stayed in.</summary>
/// <param name="State">The state after.</param>
/// <param name="StateChanged">Whether this call moved the state.</param>
public readonly record struct ConfigFilePresenceChange(
    ConfigFilePresenceState State,
    bool StateChanged);

/// <summary>
/// The watcher's event-shape state machine: delete followed by the file
/// coming back is a save whatever the gap; a delete with nothing coming
/// back past a generous backstop is a genuine deletion.
/// </summary>
/// <remarks>
/// <para>THE SHAPE replaces the shipped deletion dwell (wintty#1158). The
/// dwell measured TIME between load verdicts reporting absence, and a save
/// whose gap outlived it, which a network or synced profile can produce,
/// was accepted as a deletion mid save and reset to the defaults config.
/// Here a delete event opens <see cref="ConfigFilePresenceState.PendingDelete"/>
/// and only the file's reappearance closes it, at any delay: the machine
/// never reads a clock to decide that something is a save. The clock gates
/// one branch only, the really-gone one, where being slow is safe: a
/// genuine deletion keeps the last-loaded settings either way, and the
/// backstop only decides when the user is told.</para>
///
/// <para>THE BACKSTOP, <see cref="DefaultBackstop"/>, is set against the
/// same measurements the dwell's floor was: the widest absence window any
/// save shape leaves on this platform is 22ms, so the backstop is ~454x
/// that and ~11x the old 900ms floor. It can afford to be generous because
/// it no longer decides anything about the session's config: misclassifying
/// a slow save as a deletion costs a notice that the save's own
/// reappearance takes back down, and the running config never moves.</para>
///
/// <para>REPEATED deletes inside one pending do not re-arm the backstop:
/// its clock starts at the first delete of the episode. A provider that
/// repeats or splits its reports must not be able to postpone the notice
/// indefinitely, and a re-arm is exactly such a postponement. The next
/// episode, after a reappearance, times its own.</para>
///
/// <para>ONLY an appearance event resolves a pending delete. A Changed
/// event does not, although a changed file must have been recreated by
/// something: buffers can deliver a pre-delete write's Changed report
/// after the Deleted report, and believing it would cancel a real
/// deletion's notice and leave the session running a stale config with
/// nothing on screen saying so. The live half of the same question is the
/// opposite: <see cref="SeePresent"/> is a check run at delivery time,
/// now, not a buffered report, and it resolves. What resolves a save's
/// reload is the settle that finds the file there, which the completing
/// rename's own event guarantees.</para>
///
/// <para>ABSENCE stops being an instruction. While a delete is pending or
/// concluded, <see cref="AbsenceIsInstruction"/> is false, and the
/// watcher's delivery that finds the file missing reports nothing: there
/// is nothing to load and no verdict to observe. Only the eventless
/// absence, a delivery that finds the file gone with no delete event ever
/// seen (an overflow that ate the event, a save straddling the hop from
/// timer to delivery, a deleted directory), still reports, and the load
/// verdict machinery answers it as it always did.</para>
///
/// <para>TIME is monotonic, like every other clock in this directory. A
/// stepped-back clock restarts the pending window rather than concluding
/// early, and <see cref="UntilBackstop"/> never waits past one backstop.
/// Not thread safe, and not meant to be: every caller is the watcher's
/// posted delivery or the host's own poll, both on the thread the host
/// loads the config on.</para>
/// </remarks>
public sealed class ConfigFilePresence
{
    /// <summary>
    /// How long a delete waits for the file to come back before it is
    /// concluded. See the class remarks for the measurements behind it.
    /// </summary>
    public static readonly TimeSpan DefaultBackstop = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _backstop;
    private readonly Func<TimeSpan> _now;

    private ConfigFilePresenceState _state = ConfigFilePresenceState.Present;
    private TimeSpan _pendingSince;

    /// <param name="backstop">How long a pending delete waits. Must be
    /// positive: a zero backstop concludes on the first poll, which is the
    /// dwell's defect in a new coat.</param>
    /// <param name="now">A monotonic clock, as elapsed time from any fixed
    /// origin, for tests. Defaults to <see cref="Stopwatch"/>.</param>
    public ConfigFilePresence(TimeSpan? backstop = null, Func<TimeSpan>? now = null)
    {
        var resolved = backstop ?? DefaultBackstop;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolved, TimeSpan.Zero);

        _backstop = resolved;
        _now = now ?? MonotonicNow;
    }

    /// <summary>Where the machine stands right now.</summary>
    public ConfigFilePresenceState State => _state;

    /// <summary>
    /// Whether a delivery that finds the file missing should report it and
    /// reload. True only in <see cref="ConfigFilePresenceState.Present"/>,
    /// where the absence is news: once a delete event is seen, the machine
    /// already knows and is already classifying it.
    /// </summary>
    public bool AbsenceIsInstruction =>
        _state == ConfigFilePresenceState.Present;

    /// <summary>
    /// How long until the open pending delete concludes: the backstop less
    /// what it has already run, or the whole backstop with none open. A
    /// poll scheduled this far out is the first one that can conclude.
    /// </summary>
    public TimeSpan UntilBackstop
    {
        get
        {
            if (_state != ConfigFilePresenceState.PendingDelete) return _backstop;
            var elapsed = _now() - _pendingSince;
            if (elapsed < TimeSpan.Zero) return _backstop;
            var left = _backstop - elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Raised once per state move, on the caller's thread, after
    /// the move is recorded. The host's notice wiring hangs off this.</summary>
    public event Action<ConfigFilePresenceChange>? StateChanged;

    /// <summary>One watcher event. Changed never moves the state; Deleted
    /// opens or keeps a pending; Appeared resolves whatever was open, at
    /// any delay, because the file is back.</summary>
    public ConfigFilePresenceChange Observe(ConfigFileEventKind kind)
    {
        switch (kind)
        {
            case ConfigFileEventKind.Deleted:
                // A second delete inside one pending does not re-arm: the
                // clock belongs to the episode's first report.
                if (_state == ConfigFilePresenceState.Present)
                    return Move(ConfigFilePresenceState.PendingDelete);
                break;

            case ConfigFileEventKind.Appeared:
                if (_state != ConfigFilePresenceState.Present)
                    return Move(ConfigFilePresenceState.Present);
                break;

            // Changed: a buffered report can postdate the delete it
            // preceded, so it never resolves a pending, and a changed file
            // that is present is resolved by the delivery's own existence
            // check instead. Present stays Present.
            case ConfigFileEventKind.Changed:
                break;
        }

        return new ConfigFilePresenceChange(_state, StateChanged: false);
    }

    /// <summary>Live evidence, checked at delivery time, that the file is
    /// there now. Resolves a pending delete and re-opens a concluded one:
    /// the user, an editor or a sync client put the file back, and the
    /// settle that follows reloads it.</summary>
    public ConfigFilePresenceChange SeePresent()
    {
        if (_state == ConfigFilePresenceState.Present)
            return new ConfigFilePresenceChange(_state, StateChanged: false);
        return Move(ConfigFilePresenceState.Present);
    }

    /// <summary>The backstop ran out. Concludes an open pending whose
    /// whole backstop has elapsed; an early tick, a concluded deletion
    /// and a present file all leave the state as it was.</summary>
    public ConfigFilePresenceChange Poll()
    {
        if (_state != ConfigFilePresenceState.PendingDelete)
            return new ConfigFilePresenceChange(_state, StateChanged: false);

        // A clock stepped back inside the window restarts it here rather
        // than reading as expired-or-not on a negative elapsed: the
        // direction that never concludes early and never waits past one
        // more backstop, the same rule the deletion confirmation held. The
        // default clock cannot step; this is for the injected ones.
        var now = _now();
        if (now < _pendingSince)
            _pendingSince = now;

        if (now - _pendingSince < _backstop)
            return new ConfigFilePresenceChange(_state, StateChanged: false);

        return Move(ConfigFilePresenceState.Gone);
    }

    private ConfigFilePresenceChange Move(ConfigFilePresenceState next)
    {
        var before = _state;
        _state = next;
        if (next == ConfigFilePresenceState.PendingDelete)
        {
            // A clock that runs backwards cannot measure a backstop. The
            // default one cannot, but an injected one can, and restarting
            // the window here is the direction that never concludes early
            // and never waits past one more backstop.
            var now = _now();
            if (before != ConfigFilePresenceState.PendingDelete || now < _pendingSince)
                _pendingSince = now;
        }

        var change = new ConfigFilePresenceChange(next, StateChanged: true);
        StateChanged?.Invoke(change);
        return change;
    }

    private static TimeSpan MonotonicNow() => Stopwatch.GetElapsedTime(0);
}
