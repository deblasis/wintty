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
    /// ask for it. It does not stay refused: a settle that finds the
    /// watched file still gone a full debounce period later is a deletion
    /// and not a save, and the host lowers its count on that, which is
    /// <c>ConfigFileWatcher</c>'s vanished callback. A layered file the
    /// watcher does not watch raises no event at all, so the host asks to
    /// look again instead, and a shrink that is still a shrink after the
    /// whole ask budget is spent is a deletion the same way: see
    /// <see cref="IsPersistentShrink"/>.</para>
    /// </remarks>
    public static ConfigReloadDecision Decide(
        ConfigFilesFound found,
        int defaultFilesFound,
        int sessionDefaultFilesFound)
    {
        // "No default config file exists" and "the count of them is zero" are
        // the same statement, and the loader is written so that they always
        // are. They still cross the FFI as two separate values, so this
        // checks that they agree rather than taking it on trust. It is worth
        // the two comparisons: an Absent carrying a non-zero count walks
        // straight through the shrink test below, and what it lets through is
        // a config of pure defaults applied at every live surface, which is
        // the whole thing this gate exists to refuse.
        if ((found == ConfigFilesFound.Absent) != (defaultFilesFound == 0))
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
    /// callback's case, decided on a whole quiet period of the file being
    /// gone rather than on a count.
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
    /// budget, which no save in flight can do: each ask waits out a full
    /// quiet period, and a rename that slow has lost its race with its own
    /// editor.
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
    /// Whether a delivery that found the watched config file gone should
    /// spend one ask on looking again, rather than believing it at once.
    /// </summary>
    /// <remarks>
    /// <para>The vanish proves deletion from one observation while the
    /// shrink proves it from a spent budget, and the one-observation
    /// standard is what mid-save firing exploits.</para>
    ///
    /// <para>It fires during an ordinary atomic save. Measured, not
    /// reasoned: the watcher posts the delivery and the existence check
    /// runs inside it a dispatcher turn later, so the quiet period the
    /// debounce buys applies to the settle and not to the check, and the
    /// file need only be away for that turn.
    /// <c>ConfigFileWatcherTests</c> builds exactly that. Believing it
    /// lowered the session count mid save, which disarmed both the shrink
    /// guard and the absent guard for the next reload, and a reload landing
    /// in a second gap then applied pure defaults at every live surface
    /// (issue #1146).</para>
    ///
    /// <para>Asking again is what separates the two: the rename completing
    /// the save lands during the asks, settles, reloads and restores the
    /// count. A file still gone after the whole budget has outlived every
    /// save that could explain it.</para>
    ///
    /// <para>A session claiming no config file has nothing to confirm, so
    /// it neither asks nor lowers.</para>
    /// </remarks>
    public static bool ShouldConfirmVanish(
        int sessionDefaultFilesFound,
        int attemptsSoFar,
        int maxAttempts) =>
        sessionDefaultFilesFound > 0 && attemptsSoFar < maxAttempts;

    /// <summary>
    /// Whether a vanished watched file has outlived its whole confirmation
    /// budget, which no save in flight can do, so it is a deletion and the
    /// session should stop claiming to be running on it.
    /// </summary>
    public static bool IsPersistentVanish(
        int sessionDefaultFilesFound,
        int attemptsSoFar,
        int maxAttempts) =>
        sessionDefaultFilesFound > 0 && attemptsSoFar >= maxAttempts;
}
