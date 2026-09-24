using System;

namespace Ghostty.Core.Config;

/// <summary>
/// The wiring between what a config LOAD found and the session's vanish
/// question: what an absent verdict costs, what any other verdict restores,
/// and what a confirmed deletion causes.
/// </summary>
/// <remarks>
/// <para>This lived in <c>ConfigService</c> until nothing could prove it:
/// no test executes the shell, so the wiring was readable only as source
/// shape, and the one call that restores the budget was guarded by a
/// shape assertion and nothing else. Measured: deleting it left the rest
/// of the suite green. Here the wiring is behaviour a test drives.</para>
///
/// <para>It is driven by LOAD VERDICTS and not by watcher deliveries, which
/// is the correction wintty#1155 and the symlink case forced. A delivery
/// knows only whether the path existed at the instant it looked, and that
/// is a proxy for the question rather than an answer to it: a dangling
/// symlink exists while its open returns not-found, so the load reads it as
/// absent: restoring the budget on a settled delivery cleared what the same
/// settle's reload then spent, and the question oscillated without ever
/// concluding. A load's verdict is what the loader actually saw, and it
/// separates absent from present-but-unreadable, and it is the same evidence
/// wherever the reload came from, so a host with no watcher at all still
/// makes progress.</para>
///
/// <para>Not thread safe, and not meant to be: every caller is the
/// watcher's delivery or a reload, both of which are UI thread only.</para>
/// </remarks>
public sealed class ConfigVanishProtocol
{
    private readonly ConfigVanishConfirmer _confirmer;
    private readonly Func<int> _sessionDefaultFilesFound;
    private readonly Func<bool> _ask;
    private readonly Func<TimeSpan, bool>? _lookAgainAfter;
    private readonly Action _onAccept;

    /// <param name="sessionDefaultFilesFound">How many default config
    /// files the config in force was built from, read at each report: a
    /// session claiming none has nothing to lose and the reports are
    /// ignored.</param>
    /// <param name="ask">Schedules one more watcher delivery and answers
    /// whether it did. The watcher's own <c>Resettle</c>. It is an
    /// ACCELERATOR and nothing hangs on its answer: an earlier revision
    /// advanced only on asks that scheduled, which wedged this in the
    /// believing-nothing direction whenever no watcher existed. That is the
    /// DEFAULT configuration, since <c>auto-reload-config</c> is off, and it
    /// is the issue #676 lockout reached through its own fix
    /// (wintty#1155).</param>
    /// <param name="onAccept">A deletion was confirmed: the session stops
    /// claiming to run on files it can no longer vouch for. Runs only on
    /// the far side of the floor.</param>
    /// <param name="lookAgainAfter">Schedules one reload after the given
    /// delay and answers whether it did. Asked only when
    /// <paramref name="ask"/> scheduled nothing, with the time left until
    /// the floor, so the look it buys is the first one that can conclude.
    /// Without it a host with no watcher concludes only when something else
    /// happens to reload, and the reload that opened the stretch (a High
    /// Contrast toggle, say) is declined and lost: the user has to do it
    /// twice.</param>
    public ConfigVanishProtocol(
        Func<int> sessionDefaultFilesFound,
        Func<bool> ask,
        Action onAccept,
        TimeSpan? floor = null,
        Func<TimeSpan>? now = null,
        Func<TimeSpan, bool>? lookAgainAfter = null)
    {
        ArgumentNullException.ThrowIfNull(sessionDefaultFilesFound);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(onAccept);

        _confirmer = new ConfigVanishConfirmer(floor, now);
        _sessionDefaultFilesFound = sessionDefaultFilesFound;
        _ask = ask;
        _lookAgainAfter = lookAgainAfter;
        _onAccept = onAccept;
    }

    /// <summary>
    /// The watcher's sooner look first; failing that, one look at the floor.
    /// Either is an accelerator: the confirmer never reads the answer.
    /// </summary>
    private bool AskOrLookAgain() =>
        _ask() || _lookAgainAfter?.Invoke(_confirmer.UntilFloor) == true;

    /// <summary>
    /// A config load finished. Its verdict is the evidence: absence is an
    /// observation of the question, and anything else answers it.
    /// </summary>
    /// <returns>
    /// Whether this verdict confirmed a deletion, so the caller can apply
    /// the config it has just built rather than discarding one it has
    /// proven correct.
    /// </returns>
    /// <remarks>
    /// One observation proves nothing: an ordinary save leaves the path
    /// absent for up to 22ms measured, and a load can land in that window
    /// whoever triggered it. <see cref="ConfigVanishConfirmer"/> holds what
    /// has to hold instead, and why each part is there.
    ///
    /// Anything but absence restores the question, because both a read and
    /// an open refused for any reason but not-found (a lock, a sharing
    /// violation, an offline cloud placeholder) prove a file is there.
    /// Restoring on a file merely EXISTING is what a dangling symlink
    /// defeats: File.Exists says yes, and the open returns not-found.
    /// </remarks>
    public bool Observed(ConfigFilesFound found)
    {
        if (found != ConfigFilesFound.Absent)
        {
            _confirmer.Reset();
            return false;
        }

        if (_confirmer.Observe(_sessionDefaultFilesFound(), AskOrLookAgain)
            != ConfigVanishAction.Accept)
        {
            return false;
        }

        _onAccept();
        return true;
    }
}
