using System;

namespace Ghostty.Core.Config;

/// <summary>
/// The wiring between a config watcher's deliveries and the session's
/// vanish budget: what a settled delivery does to it, what a vanished one
/// costs, and what a confirmed deletion causes.
/// </summary>
/// <remarks>
/// <para>This lived in <c>ConfigService</c> until nothing could prove it:
/// no test executes the shell, so the wiring was readable only as source
/// shape, and the one call that restores the budget was guarded by a
/// shape assertion and nothing else. Measured: deleting it left the rest
/// of the suite green. Here the wiring is behaviour a test drives against
/// a real watcher, and the shell's part is a delegation.</para>
///
/// <para>Not thread safe, and not meant to be: every caller is the
/// watcher's delivery or a reload, both of which are UI thread only.</para>
/// </remarks>
public sealed class ConfigVanishProtocol
{
    private readonly ConfigVanishConfirmer _confirmer;
    private readonly Func<int> _sessionDefaultFilesFound;
    private readonly Func<bool> _ask;
    private readonly Action _onAccept;

    /// <param name="sessionDefaultFilesFound">How many default config
    /// files the config in force was built from, read at each report: a
    /// session claiming none has nothing to lose and the reports are
    /// ignored.</param>
    /// <param name="ask">Schedules one more watcher delivery and answers
    /// whether it did. The watcher's own <c>Resettle</c>: an ask that
    /// schedules nothing is not an ask, and the budget only spends on
    /// real ones, so a constant here wedges the protocol in the
    /// believing-nothing direction, which is the issue #676 lockout.</param>
    /// <param name="onAccept">A deletion was confirmed: the session stops
    /// claiming to run on files it can no longer vouch for. Runs only on
    /// the far side of the whole ask budget.</param>
    public ConfigVanishProtocol(
        Func<int> sessionDefaultFilesFound,
        Func<bool> ask,
        Action onAccept,
        int maxAttempts = ConfigVanishConfirmer.DefaultAttempts)
    {
        ArgumentNullException.ThrowIfNull(sessionDefaultFilesFound);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(onAccept);

        _confirmer = new ConfigVanishConfirmer(maxAttempts);
        _sessionDefaultFilesFound = sessionDefaultFilesFound;
        _ask = ask;
        _onAccept = onAccept;
    }

    /// <summary>
    /// A delivery found the file present, which is the evidence that
    /// answers any open vanish question. The budget is restored here and
    /// not on the applied reload that may follow: a file that comes back
    /// and will not open reaches this and never reaches an applied
    /// reload, and a budget left spent there has the next ordinary save
    /// believed on one observation.
    /// </summary>
    public void Settled() => _confirmer.Reset();

    /// <summary>
    /// A delivery found the file gone. One observation is something an
    /// ordinary save produces, so it spends an ask rather than concluding;
    /// only a report that outlives the whole budget does that, and the
    /// accept side effect runs then and only then.
    /// </summary>
    public void Vanished()
    {
        if (_confirmer.Observe(_sessionDefaultFilesFound(), _ask)
            == ConfigVanishAction.Accept)
        {
            _onAccept();
        }
    }
}
