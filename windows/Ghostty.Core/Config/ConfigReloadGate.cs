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
    /// ask for it. It does not stay refused: a settle that finds the file
    /// still gone a full debounce period later is a deletion and not a
    /// save, and the host lowers its count on that, which is
    /// <c>ConfigFileWatcher</c>'s vanished callback.</para>
    /// </remarks>
    public static ConfigReloadDecision Decide(
        ConfigFilesFound found,
        int defaultFilesFound,
        int sessionDefaultFilesFound) => found switch
        {
            // Absent and Loaded differ only in whether the count is zero, so
            // the comparison below is the whole rule for both. Spelling them
            // as one case rather than two keeps there being one rule.
            ConfigFilesFound.Loaded or ConfigFilesFound.Absent =>
                defaultFilesFound < sessionDefaultFilesFound
                    ? ConfigReloadDecision.Decline
                    : ConfigReloadDecision.Apply,

            // Unreadable, and anything that is none of the three: a value
            // outside the enum means the two sides of the FFI disagree and
            // the config in hand cannot be trusted.
            _ => ConfigReloadDecision.Decline,
        };

    /// <summary>
    /// Whether a declined reload should ask to be tried again.
    ///
    /// Only for <c>Unreadable</c>: that save has landed and its filesystem
    /// events are spent, so nothing else will ask. A count that shrank needs
    /// no retry, because the rename that completes the save raises its own
    /// event, and a file that never comes back is reported as vanished
    /// instead.
    /// </summary>
    public static bool ShouldRetry(ConfigFilesFound found, int attemptsSoFar, int maxAttempts) =>
        found == ConfigFilesFound.Unreadable && attemptsSoFar < maxAttempts;
}
