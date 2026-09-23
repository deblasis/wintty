using System.IO;

namespace Ghostty.Core.Profiles;

/// <summary>
/// What a launch's first pane runs when the launch named no profile: the
/// command after <c>-e</c>, else the configured <c>command</c> key, else the
/// default profile's own command. This is Windows Terminal's order for
/// <c>wt -- cmd</c> and for a default profile's <c>commandline</c>, and it
/// applies to a cold start and to a launch forwarded to a running instance
/// alike (deblasis/wintty#1136).
/// </summary>
/// <remarks>
/// <para>
/// It has to be decided here, on the snapshot, because the snapshot is what
/// the pane spawns from. A profile snapshot's <c>ResolvedCommand</c> becomes
/// the surface's own command, which replaces libghostty's <c>command</c> key
/// outright, and a persistent pane hands that same string to the sessions
/// daemon, which never sees libghostty's config at all. Leaving the choice
/// to libghostty's first-surface <c>initial-command</c> therefore lost the
/// configured command on every build and lost <c>-e</c> on any pane the
/// daemon spawns.
/// </para>
/// <para>
/// A launch that named a profile (a jump-list profile entry) keeps that
/// profile's command: the user picked it, so neither the configured command
/// nor a default applies.
/// </para>
/// Pure (no I/O) so the rules are unit-testable without a GUI.
/// </remarks>
public static class FirstPaneCommand
{
    /// <summary>
    /// The command a launch's first pane runs in place of the profile's, or
    /// null when the profile's own command stands.
    /// </summary>
    /// <param name="launchCommand">The <c>-e</c> command, already rendered
    /// as one string (<see cref="SingleInstance.LaunchCommand.FromArgs"/>).</param>
    /// <param name="configuredCommand">The configured <c>command</c> key.</param>
    public static string? Pick(string? launchCommand, string? configuredCommand)
    {
        if (!string.IsNullOrWhiteSpace(launchCommand)) return launchCommand;
        if (!string.IsNullOrWhiteSpace(configuredCommand)) return configuredCommand.Trim();
        return null;
    }

    /// <summary>
    /// <paramref name="snapshot"/> running <paramref name="command"/>. The
    /// profile's identity (name, icon, visuals, working directory) is kept,
    /// the way <c>wt -- cmd</c> keeps the default profile's. With no profile
    /// to carry it (an empty registry), a minimal snapshot is made, because a
    /// null snapshot hands the choice back to libghostty, which honours
    /// <c>-e</c> only on the process's very first surface: a forwarded
    /// launch's pane would silently run the default shell instead.
    /// </summary>
    public static ProfileSnapshot? Apply(
        ProfileSnapshot? snapshot,
        string? command,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return snapshot;

        if (snapshot is not null)
            return snapshot with { ResolvedCommand = command };

        return new ProfileSnapshot(
            ProfileId: "",
            Version: 0,
            ResolvedCommand: command,
            WorkingDirectory: string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            DisplayName: DisplayNameFor(command),
            Icon: new IconSpec.BundledKey("default"),
            Visuals: EffectiveVisualOverrides.Empty);
    }

    // The program the command runs, without its extension, which is what a
    // tab would show once the process reports its title anyway.
    private static string DisplayNameFor(string command)
    {
        var exe = ProfileOrderResolver.CommandBasename(command);
        return exe is null ? command.Trim() : Path.GetFileNameWithoutExtension(exe);
    }
}
