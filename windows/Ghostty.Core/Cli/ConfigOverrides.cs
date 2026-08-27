namespace Ghostty.Core.Cli;

/// <summary>
/// What an invocation asks of the configuration, and the command line
/// to hand libghostty once the Wintty spellings are translated.
/// </summary>
/// <param name="CommandLine">
/// The command line with every <c>--no-config</c> replaced by the
/// libghostty key it stands for. Unchanged when the flag is absent, so
/// the common launch allocates nothing.
/// </param>
/// <param name="NoConfig">
/// Whether the invocation asked for no configuration at all. Wintty
/// reads several Windows-only keys off the config file itself, without
/// libghostty, so this has to travel past the rewrite: rewriting alone
/// would leave those keys in force and make the flag a half-truth.
/// </param>
/// <param name="ConfigFile">
/// Whether the invocation named a config file of its own.
/// </param>
public readonly record struct ConfigOverrides(
    string CommandLine,
    bool NoConfig,
    bool ConfigFile)
{
    /// <summary>
    /// Whether this invocation wants a configuration that a process
    /// already running cannot be holding.
    /// </summary>
    public bool Any => NoConfig || ConfigFile;
}
