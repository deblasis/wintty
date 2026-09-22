using System;

namespace Ghostty.Core.Config;

/// <summary>What a report that the watched config file is gone should cause.</summary>
public enum ConfigVanishAction
{
    /// <summary>Nothing. The session claims no config file to lose.</summary>
    Ignore,

    /// <summary>Ask for another delivery, and believe nothing yet.</summary>
    Confirm,

    /// <summary>The asks are spent and it is still gone: a deletion.</summary>
    Accept,
}

/// <summary>
/// How many times a vanished config file has to be seen before it counts as
/// deleted, and the count of how many times it has been.
/// </summary>
/// <remarks>
/// <para>The vanish proves deletion from one observation while a shrink
/// proves it from a spent budget, and the one-observation standard is what
/// mid-save firing exploits (issue #1146).</para>
///
/// <para>This is a class in Ghostty.Core rather than a counter and a const
/// in the host for one reason: nothing executes the host. Ghostty.Tests has
/// no reference to the shell project, so every test about
/// <c>ConfigService</c> reads its source with Roslyn and asserts on shape.
/// That cannot see a budget of zero, which turns the asks off and restores
/// the defect exactly; it cannot see an increment moved out from behind the
/// scheduled-ask check either. Both of those were measured passing against
/// the source-shape tests. The state and the arithmetic live here so a test
/// can drive them instead of reading them.</para>
///
/// <para>Not thread safe, and not meant to be: every caller is the config
/// watcher's delivery or a reload, both of which are UI thread only.</para>
/// </remarks>
public sealed class ConfigVanishConfirmer
{
    /// <summary>
    /// Asks before a vanish is believed. Three debounce periods is about a
    /// second, which outlasts every save in ordinary disk conditions and
    /// still settles a real deletion quickly enough that the next High
    /// Contrast toggle is not left waiting on it. It is a heuristic bound,
    /// not a proof: a swap held wedged longer than the budget, by a frozen
    /// editor or a network rename that lost its race, is confirmed
    /// wrongly. That is #1146 narrowed by the budget rather than closed,
    /// and it heals on the completing settle.
    /// </summary>
    public const int DefaultAttempts = 3;

    private readonly int _maxAttempts;
    private int _attempts;

    /// <param name="maxAttempts">Asks to spend before believing it. Must be
    /// at least one: a budget of zero is the defect this exists to prevent,
    /// so it is refused here rather than left to a reviewer to notice.</param>
    public ConfigVanishConfirmer(int maxAttempts = DefaultAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
    }

    /// <summary>Asks spent so far in this stretch.</summary>
    public int Attempts => _attempts;

    /// <summary>
    /// A delivery found the watched file gone. <paramref name="ask"/> is
    /// asked to schedule one more delivery and says whether it did.
    /// </summary>
    /// <remarks>
    /// An ask the watcher dropped was never put, so it does not count:
    /// spending the budget on it would confirm a deletion out of silence.
    ///
    /// The conclusion restores the budget. A stretch that reached Accept
    /// is over, and a spent budget carried past its own answer accepts
    /// the next single report at once: the file can come back through
    /// paths that settle no delivery, because the app's own writes
    /// suppress the watcher, and the count those restores raise is what
    /// makes the next mid-save report dangerous to believe. While the
    /// count the Accept zeroed still stands, reports are Ignored below,
    /// so the restored budget is inert until it is needed.
    /// </remarks>
    public ConfigVanishAction Observe(int sessionDefaultFilesFound, Func<bool> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!ConfigReloadGate.ShouldConfirmVanish(
                sessionDefaultFilesFound, _attempts, _maxAttempts))
        {
            var accept = ConfigReloadGate.IsPersistentVanish(
                sessionDefaultFilesFound, _attempts, _maxAttempts);
            if (accept) _attempts = 0;
            return accept ? ConfigVanishAction.Accept : ConfigVanishAction.Ignore;
        }

        if (ask()) _attempts++;
        return ConfigVanishAction.Confirm;
    }

    /// <summary>
    /// Evidence arrived that answers the question: the file is there, or a
    /// reload applied. The next vanish is a fresh question.
    /// </summary>
    /// <remarks>
    /// Resetting only on an applied reload is not enough, and the gap is
    /// reachable: a stretch that spends the budget, then a file that comes
    /// back but will not open for long enough to exhaust the reload's own
    /// retries, leaves this spent with no applied reload to clear it. The
    /// next ordinary save that straddles the delivery hop is then believed
    /// on one observation, which is issue #1146 re-entered through a stale
    /// budget. Call this wherever the file is seen present.
    ///
    /// The conclusion guards the same invariant from its own side: an
    /// Accept restores the budget too, because the file can return without
    /// any delivery ever seeing it (the app's own suppressed writes). See
    /// <see cref="Observe"/>.
    /// </remarks>
    public void Reset() => _attempts = 0;
}
