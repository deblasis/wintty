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
    /// <param name="sessionHasConfigFile">Whether the config currently in
    /// force was built from a config file that exists.</param>
    /// <remarks>
    /// <para><c>Loaded</c> always applies, and that includes an empty config
    /// file: emptying it is a configuration that asks for nothing, so the
    /// defaults come back, which is what it says.</para>
    ///
    /// <para><c>Unreadable</c> never applies. A config file that is there and
    /// will not open is an editor, an indexer or a sync client holding it,
    /// and the config built from it carries defaults where the user's
    /// settings belong. Applying it resets a terminal they are looking at for
    /// the length of somebody else's file handle.</para>
    ///
    /// <para><c>Absent</c> applies only for a session that never had a config
    /// file. For one that has, no file is what the gap of an atomic save
    /// looks like, and the rename that completes the save is moments away.
    /// For one that never had, refusing would be wrong in the other
    /// direction: it is the path a High Contrast change takes on a machine
    /// with no config file at all, and that override has to reach the
    /// terminal.</para>
    ///
    /// <para>A config file deleted on purpose reads as the save gap and is
    /// treated the same way: the session keeps its settings until it is
    /// restarted. That is deliberate. The alternative, reading a deletion as
    /// "put everything back to its default", tears down what is on screen for
    /// an act that did not ask for it.</para>
    /// </remarks>
    public static ConfigReloadDecision Decide(
        ConfigFilesFound found,
        bool sessionHasConfigFile) => found switch
        {
            ConfigFilesFound.Loaded => ConfigReloadDecision.Apply,
            ConfigFilesFound.Unreadable => ConfigReloadDecision.Decline,
            ConfigFilesFound.Absent => sessionHasConfigFile
                ? ConfigReloadDecision.Decline
                : ConfigReloadDecision.Apply,
            _ => ConfigReloadDecision.Decline,
        };

    /// <summary>
    /// Whether the session is running on a config file once
    /// <paramref name="found"/> has been applied. Only a load that read one
    /// is applied with a file behind it, so this can only go false for a
    /// session that never had one.
    /// </summary>
    public static bool HasConfigFileAfterApply(ConfigFilesFound found) =>
        found == ConfigFilesFound.Loaded;

    /// <summary>
    /// Whether a declined reload should ask to be tried again.
    ///
    /// Only for <c>Unreadable</c>: that save has landed and its filesystem
    /// events are spent, so nothing else will ask. <c>Absent</c> needs no
    /// retry, because the rename that completes the save raises its own.
    /// </summary>
    public static bool ShouldRetry(ConfigFilesFound found, int attemptsSoFar, int maxAttempts) =>
        found == ConfigFilesFound.Unreadable && attemptsSoFar < maxAttempts;
}
