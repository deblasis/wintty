namespace Ghostty.Core.Config;

/// <summary>
/// What a default-file load found, as the reload gate reads it. Mirrors
/// <c>ghostty_config_default_files_e</c>; the interop enum is not used here
/// because this assembly holds no P/Invoke surface and the gate is meant to
/// be testable without libghostty.
/// </summary>
public enum ConfigFilesFound
{
    /// <summary>A config file is there and would not open.</summary>
    Unreadable = -1,

    /// <summary>No config file exists at any default location.</summary>
    Absent = 0,

    /// <summary>A config file was read. An empty one counts.</summary>
    Loaded = 1,
}

/// <summary>What a reload should do with the config it just built.</summary>
public enum ConfigReloadDecision
{
    /// <summary>Push it at the running app.</summary>
    Apply,

    /// <summary>Free it and keep the config already in force.</summary>
    Decline,
}

/// <summary>
/// Whether a rebuilt config is the user's, and therefore safe to apply.
///
/// This is the rule the whole of issue #676 turns on, so it lives on its own
/// rather than inline in <c>ConfigService.Reload</c>: the shell assembly
/// cannot be referenced from a test host, and a rule this cheap to get
/// backwards should not be pinned only by reading the source.
/// </summary>
public static class ConfigReloadGate
{
    /// <param name="found">What the load reported.</param>
    /// <param name="defaultFilesFound">How many default config files exist,
    /// readable or not, as the same load counted them.</param>
    /// <param name="sessionDefaultFilesFound">How many existed when the
    /// config currently in force was built.</param>
    /// <remarks>
    /// <para>There are several default config files and they are layered,
    /// not alternatives: the pre-rename <c>ghostty/config.ghostty</c> is
    /// still read, and a user migrated from Ghostty has it alongside
    /// <c>wintty/config.wintty</c>. So "a config file was read" is not
    /// enough to say the config is the user's. The one being saved can be
    /// the one that is missing while the other still reads, and that load
    /// reports <c>Loaded</c> with one file fewer. Comparing the counts is
    /// what sees it; the verdict on its own cannot.</para>
    ///
    /// <para><c>Unreadable</c> never applies. A config file that is there
    /// and will not open is an editor, an indexer or a sync client holding
    /// it, and the config built from it carries defaults where the user's
    /// settings belong. Applying it resets a terminal they are looking at
    /// for the length of somebody else's file handle.</para>
    ///
    /// <para>Otherwise it applies unless a default config file the session
    /// was running on is not there now. That is what the gap of an atomic
    /// save looks like from outside, and the rename that completes the save
    /// is moments away.</para>
    ///
    /// <para>Losing the last one is the same rule with the count at zero,
    /// and the other direction matters as much: a session that never had a
    /// config file still applies, because that is the path a High Contrast
    /// change takes on a machine with no config file at all, and that
    /// override has to reach the terminal.</para>
    ///
    /// <para>A config file deleted on purpose looks exactly like the save
    /// gap here, and is refused the same way, so the session keeps its
    /// settings rather than having them torn down by an act that did not
    /// ask for it. It does not stay refused: a delivery that finds the
    /// watched file gone reports it, and a stretch of absence outlasting the
    /// widest measured save gap by a wide margin is a deletion, on which the
    /// host lowers its count. One report is not, because an ordinary save
    /// produces one: see <c>ConfigVanishConfirmer</c>, which holds that
    /// question and the measurements it is pinned against. A layered file
    /// the watcher does not watch raises no event at all, and a shrink after
    /// its own budget is a deletion the same way: see
    /// <see cref="IsPersistentShrink"/>.</para>
    /// </remarks>
    public static ConfigReloadDecision Decide(
        ConfigFilesFound found,
        int defaultFilesFound,
        int sessionDefaultFilesFound)
    {
        if (!VerdictAndCountAgree(found, defaultFilesFound))
            return ConfigReloadDecision.Decline;

        return found switch
        {
            // One rule for both, because with the two agreeing they differ
            // only in whether the count is zero.
            ConfigFilesFound.Loaded or ConfigFilesFound.Absent =>
                defaultFilesFound < sessionDefaultFilesFound
                    ? ConfigReloadDecision.Decline
                    : ConfigReloadDecision.Apply,

            // Unreadable, and anything that is none of the three: a value
            // outside the enum means the two sides of the FFI disagree and
            // the config in hand cannot be trusted.
            _ => ConfigReloadDecision.Decline,
        };
    }

    /// <summary>
    /// Whether the load's verdict and its own file count say the same thing,
    /// which is the precondition for trusting either.
    /// </summary>
    /// <remarks>
    /// <para>"No default config file exists" and "the count of them is zero"
    /// are the same statement, and the loader is written so that they always
    /// are. They still cross the FFI as two separate values, so this checks
    /// that they agree rather than taking it on trust. It is worth the two
    /// comparisons: an Absent carrying a non-zero count walks straight
    /// through the shrink comparison in <see cref="Decide"/>, and what it
    /// lets through is a config of pure defaults applied at every live
    /// surface, which is the whole thing this gate exists to refuse.</para>
    ///
    /// <para>Public because the vanish wiring asks the same question about
    /// the same pair: a verdict its own count contradicts is not evidence of
    /// an absence, it is evidence of a disagreement, and must not confirm a
    /// deletion the gate refuses. One definition, so the two cannot drift
    /// into checking different things.</para>
    /// </remarks>
    public static bool VerdictAndCountAgree(
        ConfigFilesFound found,
        int defaultFilesFound) =>
        (found == ConfigFilesFound.Absent) == (defaultFilesFound == 0);

    /// <summary>
    /// Whether a declined reload should ask to be tried again.
    ///
    /// Only for <c>Unreadable</c>: that save has landed and its filesystem
    /// events are spent, so nothing else will ask. A count that shrank does
    /// not take this path, because the rename that completes the save raises
    /// its own event, and a file that never comes back is reported as
    /// vanished or confirmed as a deletion instead.
    /// </summary>
    public static bool ShouldRetry(ConfigFilesFound found, int attemptsSoFar, int maxAttempts) =>
        found == ConfigFilesFound.Unreadable && attemptsSoFar < maxAttempts;

    /// <summary>
    /// Whether the load found fewer default config files than the session
    /// is running on: <c>Loaded</c> with a count that dropped.
    /// </summary>
    /// <remarks>
    /// Either a save is mid swap, or a file is gone for good, and the load
    /// cannot tell those apart; only what happens next can. <c>Absent</c> is
    /// deliberately not one: the watched file going missing is the vanished
    /// callback's case, decided on its own budget of asks rather than on a
    /// count.
    /// </remarks>
    public static bool IsCountShrink(
        ConfigFilesFound found,
        int defaultFilesFound,
        int sessionDefaultFilesFound) =>
        found == ConfigFilesFound.Loaded
            && defaultFilesFound < sessionDefaultFilesFound;

    /// <summary>
    /// Whether a count-shrink decline should spend one ask of the shrink
    /// confirmation budget on looking again. A budget of its own, not the
    /// one <see cref="ShouldRetry"/> spends on a locked file: asks about a
    /// file that went away are not asks about a file that would not open,
    /// and one counter holding both let a gave-up locked-file stretch
    /// arrive spent here.
    /// </summary>
    /// <remarks>
    /// The ask is the confirmation protocol for deletions the watcher
    /// cannot see: it watches one path, and the default files are layered
    /// candidates, so a deleted file it does not watch raises no event at
    /// all. While the budget lasts, a save mid swap is still the expected
    /// answer, and its completing rename settles and reloads with the count
    /// restored, ending the asks.
    /// </remarks>
    public static bool ShouldConfirmShrink(
        ConfigFilesFound found,
        int defaultFilesFound,
        int sessionDefaultFilesFound,
        int attemptsSoFar,
        int maxAttempts) =>
        IsCountShrink(found, defaultFilesFound, sessionDefaultFilesFound)
            && attemptsSoFar < maxAttempts;

    /// <summary>
    /// Whether a count shrink has outlived the whole shrink confirmation
    /// budget, which no ordinary save in flight can do: each ask waits out
    /// a full quiet period, and a rename that slow has lost its race with
    /// its own editor. A wedged swap can outstay the budget all the same,
    /// and is then applied wrongly, transiently, until its settle
    /// restores the count.
    /// </summary>
    /// <remarks>
    /// So it is a deletion, of a layered file the watcher does not watch,
    /// and the host should believe the disk: apply what the load built and
    /// let the applied reload record the lower count. Refusing instead is
    /// permanent, because nothing else lowers the session count; that is
    /// the accessibility lockout of issue #676 all over again, one layer
    /// removed.
    /// </remarks>
    public static bool IsPersistentShrink(
        ConfigFilesFound found,
        int defaultFilesFound,
        int sessionDefaultFilesFound,
        int attemptsSoFar,
        int maxAttempts) =>
        IsCountShrink(found, defaultFilesFound, sessionDefaultFilesFound)
            && attemptsSoFar >= maxAttempts;

    /// <summary>
    /// Whether this look read a default config file as empty in a way the
    /// session has not seen before: the empty count grew past the one the
    /// config in force was built from.
    /// </summary>
    /// <remarks>
    /// <para>An in-place save passes through a moment where the file is
    /// present and zero bytes, and a load landing there is indistinguishable
    /// from a file emptied on purpose: both read as a configuration that
    /// asks for nothing. The verdict folds the empty read into
    /// <c>Loaded</c>, which is right about what the disk says; this asks the
    /// other question, how the read went, from the count the loader reports
    /// beside it (issue #1138).</para>
    ///
    /// <para>Only while the session is running on a config that had
    /// something in it. A session with no config file has nothing to
    /// protect, and its first save applies whatever it is, empty included.</para>
    ///
    /// <para>And only when the emptiness is news. A look whose counts match
    /// the record behind the running config is the session's own steady
    /// state, not a save in flight: applying it rebuilds the config already
    /// in force. This is the release half of the rule, twice over. A file
    /// emptied on purpose is held while the count climbs, and once the
    /// all-empty read is what is in force, further empty looks match the
    /// record and apply. And a session running one permanently empty layer
    /// beside a content one, the state an interrupted pre-fix in-place save
    /// leaves behind, is not taxed with a hold on every reload it will ever
    /// do, because its steady looks read exactly what the record says.</para>
    ///
    /// <para>What the counts see, and the assumption under them. The count
    /// is a total across every candidate the loader reads, and only one of
    /// those layers is the one the watcher watches. A truncate of a layer
    /// that had content raises the count past the record and is held,
    /// wherever the layer sits, watched or not. An atomic save (write
    /// beside, rename over) reads the old bytes in its window, counts
    /// unchanged, and is waved through: what applies is the config already
    /// in force, and the completing rename raises its own event. The wave
    /// trusts the record, and the record is a snapshot: a layer the watcher
    /// does not watch can change between the record and the window, and a
    /// fill of a recorded-empty layer cancels a watched truncate inside the
    /// totals, so one mid-save look can present record-equal counts and
    /// apply the save's own half-state. That takes an already-degraded
    /// record, a second writer, and one look; the completing write re-fires
    /// and the next look applies the true end state. A completed content
    /// edit also matches the counts and applies by design. The counts
    /// answer whether this emptiness is news, not whether the record is
    /// still true.</para>
    /// </remarks>
    public static bool IsEmptyRead(
        ConfigFilesFound found,
        int emptyReads,
        int sessionDefaultFilesFound,
        int sessionEmptyReads) =>
        found == ConfigFilesFound.Loaded
            && emptyReads > sessionEmptyReads
            && sessionDefaultFilesFound > 0;

    /// <summary>
    /// Whether an empty read has been seen enough times running to apply.
    /// A count of consecutive looks, and every look answers the question,
    /// scheduled or not: an empty load is cheap, unlike the unreadable path
    /// whose loader retries are the reason the other two budgets count only
    /// scheduled asks.
    /// </summary>
    public static bool ShouldApplyEmptyRead(int looksSoFar, int maxLooks) =>
        looksSoFar >= maxLooks;
}
