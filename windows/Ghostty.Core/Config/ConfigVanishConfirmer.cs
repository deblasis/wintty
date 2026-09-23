using System;

namespace Ghostty.Core.Config;

/// <summary>What a load reporting no config file should cause.</summary>
public enum ConfigVanishAction
{
    /// <summary>Nothing. The session claims no config file to lose.</summary>
    Ignore,

    /// <summary>Not yet proven. Keep the count, and look again.</summary>
    Confirm,

    /// <summary>Proven gone. The session may stop claiming the file.</summary>
    Accept,
}

/// <summary>
/// Decides when a config file that keeps reading as absent has actually been
/// deleted, rather than being between the two halves of a save.
/// </summary>
/// <remarks>
/// <para>An observation is a LOAD that reported no default config file. Not
/// a file-existence check, and not a request for one: the load read the disk
/// and came back with nothing, which is the only thing that answers the
/// question. Driving this off anything else is what issue #1146 and the
/// first two revisions of this class got wrong, in both directions: gating
/// on scheduling a FUTURE look when the look had already happened, and
/// resetting on <c>File.Exists</c>, which a dangling symlink satisfies while
/// the open fails.</para>
///
/// <para>Two things have to hold, and they rule out different hazards. Each
/// is pinned separately, and a mutation of either kills a different test.</para>
///
/// <para>THE FLOOR rules out a single absence window producing every
/// observation. Measured on this platform, 150 saves per pattern at roughly
/// 40k existence samples each, the widest window any save shape leaves is
/// 22.0ms for rename-away-then-create, with <c>ReplaceFile</c> at 21.2ms and
/// delete-then-rename at 8.4ms; <c>MoveFileEx</c> with REPLACE_EXISTING
/// leaves none. <see cref="DefaultFloor"/> is 900ms, about 41 times the
/// widest, and that multiple is asserted against the measurement rather than
/// left as a round number.</para>
///
/// <para>Note what the floor already implies. It is measured from the FIRST
/// observation, so at that one the elapsed time is zero and it can never
/// accept: two observations are required by the floor itself. An earlier
/// revision carried a separate count of two beside it, which therefore could
/// not bind, and read like a guard while providing nothing.</para>
///
/// <para>THE RESET rules out a stale budget outliving its question, and it
/// is also what resists repeated correlated sampling. Observations here are
/// CAUSED by saves, which is issue #1146's own mechanism, so successive ones
/// are not independent of successive save gaps. Any load that did not report
/// absence proves a file is there and starts the next absence from nothing.
/// For two observations to both count, no load may have found the file
/// between them, and on the watcher's path a completed save raises an event
/// whose delivery returns Loaded within about 300ms. So two counted
/// observations 900ms apart need 900ms of continuous re-arming with no
/// successful load, which produces ONE delivery rather than two. Where no
/// watcher exists the observations are a reload the user caused (a High
/// Contrast toggle) and the one look the host schedules a floor later, so it
/// needs both to land inside two different saves' gaps, 900ms apart.
/// Deleting the reset removes both of those, not merely some tidiness.</para>
///
/// <para>WHAT SURVIVES, stated as the probability argument it is. A save
/// that stalls longer than the floor between its delete and its rename
/// defeats this: every observation inside that gap reports absence, nothing
/// intervenes to reset, and the deletion is accepted mid save. No count of
/// observations changes that, because a further observation is inside the
/// same gap; only a longer floor would. A network drive, a scanner or a sync
/// client holding the file could in principle exceed 900ms. The defences are
/// the 41x margin over the widest measured window and the reset, and neither
/// is a proof.</para>
///
/// <para>Not thread safe, and not meant to be: every caller is the config
/// watcher's delivery or a reload, both UI thread only.</para>
/// </remarks>
public sealed class ConfigVanishConfirmer
{
    /// <summary>
    /// Wall time that must separate the first observation from the one that
    /// accepts. See the class remarks for the measurements behind it.
    /// </summary>
    public static readonly TimeSpan DefaultFloor = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// The widest absence window measured across save shapes on this
    /// platform, which <see cref="DefaultFloor"/> is set as a multiple of.
    /// Here so a test can assert the relationship rather than the number.
    /// </summary>
    public static readonly TimeSpan WidestMeasuredSaveWindow =
        TimeSpan.FromMilliseconds(22);

    private readonly TimeSpan _floor;
    private readonly Func<DateTimeOffset> _now;

    private bool _seen;
    private DateTimeOffset _first;

    /// <param name="floor">Wall time that must separate the first
    /// observation from the accepting one. Must be positive: a floor of zero
    /// accepts the first report, which is the defect this exists to prevent,
    /// so it is refused here rather than left for a reviewer to notice.
    /// Because it is measured from the first observation, a positive floor
    /// is also what makes a second observation necessary.</param>
    /// <param name="now">Clock, for tests. Defaults to UTC now.</param>
    public ConfigVanishConfirmer(
        TimeSpan? floor = null,
        Func<DateTimeOffset>? now = null)
    {
        var resolved = floor ?? DefaultFloor;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolved, TimeSpan.Zero);

        _floor = resolved;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether a stretch of absence is currently open.</summary>
    public bool Observing => _seen;

    /// <summary>
    /// How long until an observation would accept: the floor less what the
    /// open stretch has already run, or the whole floor with none open.
    /// A look scheduled this far out is the first one that can conclude.
    /// </summary>
    public TimeSpan UntilFloor
    {
        get
        {
            if (!_seen) return _floor;
            var left = _floor - (_now() - _first);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>A load reported no default config file.</summary>
    /// <param name="sessionDefaultFilesFound">How many the session believes
    /// it is running on. Zero means nothing to lose and nothing to
    /// confirm.</param>
    /// <param name="ask">Asks for another look sooner than one would
    /// otherwise arrive, and reports whether it scheduled one.</param>
    /// <remarks>
    /// <paramref name="ask"/> is an ACCELERATOR, not a gate. The observation
    /// has already happened, so whether another can be scheduled says
    /// nothing about it. Gating on it was this class's second revision, and
    /// it made a deletion unprovable whenever no watcher existed, which is
    /// the DEFAULT configuration: with <c>auto-reload-config</c> off there
    /// is no watcher to ask, the ask always failed, nothing ever advanced,
    /// and the session refused every reload for the life of the process.
    /// That is issue #676 reached through its own fix.
    /// </remarks>
    public ConfigVanishAction Observe(int sessionDefaultFilesFound, Func<bool> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (sessionDefaultFilesFound <= 0) return ConfigVanishAction.Ignore;

        var now = _now();
        if (!_seen)
        {
            _seen = true;
            _first = now;
        }

        // Measured from the first observation, so this is false for it
        // however long the stretch later runs. That is what makes a second
        // observation necessary, and it is the whole of the count.
        if (now - _first >= _floor) return ConfigVanishAction.Accept;

        // Only while still asking: accepting needs no further look.
        ask();
        return ConfigVanishAction.Confirm;
    }

    /// <summary>
    /// A load proved a config file is there, so the next absence is a fresh
    /// question.
    /// </summary>
    /// <remarks>
    /// Driven by the load's verdict, not by the file appearing to exist. A
    /// dangling symlink satisfies <c>File.Exists</c> and fails to open, so
    /// resetting on existence cleared the stretch that the same settle's
    /// reload then started, and it oscillated without ever reaching a
    /// conclusion.
    ///
    /// This is also the whole defence against correlated sampling; see the
    /// class remarks before removing it.
    /// </remarks>
    public void Reset() => _seen = false;
}
