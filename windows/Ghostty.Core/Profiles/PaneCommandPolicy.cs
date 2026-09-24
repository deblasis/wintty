using System;
using System.IO;

namespace Ghostty.Core.Profiles;

/// <summary>
/// The configured <c>command</c> key as the user set it: its text, and
/// whether it was a <c>direct:</c> command, in which case
/// <see cref="Text"/> is an argv quoted by the Windows command-line rules.
/// </summary>
public readonly record struct ConfiguredCommand(string Text, bool IsArgv);

/// <summary>
/// What a new pane runs (deblasis/wintty#1136). In order:
/// <list type="number">
/// <item>A profile the user picked for a tab or window (the new-tab menu,
/// the jump list, the palette) runs that profile. Nothing here applies.</item>
/// <item><c>-e</c> runs in the first pane of the window its launch opens,
/// and nowhere else (<see cref="LaunchFirstPane"/>). Panes opened from that
/// pane do not inherit it (<see cref="Inherit"/>).</item>
/// <item>An explicitly set <c>default-profile</c> runs in every other new
/// pane, and the configured <c>command</c> is not used.</item>
/// <item>Without one, a configured <c>command</c> runs in every other new
/// pane: the first pane, new tabs, splits, the quick terminal
/// (<see cref="ImplicitDefault"/>).</item>
/// <item>Otherwise the registry's default profile (its first visible one)
/// runs.</item>
/// </list>
/// It is decided on the snapshot because the snapshot is what a pane spawns
/// from: its <c>ResolvedCommand</c> replaces libghostty's own <c>command</c>
/// key, and a persistent pane hands it to the sessions daemon, which never
/// sees libghostty's config.
/// <para>
/// An argv (<c>-e</c>, or a <c>direct:</c> command) travels flagged
/// (<see cref="ProfileSnapshot.CommandIsArgv"/>) and reaches the surface as
/// <see cref="SurfaceCommand"/>, so it is run directly and never handed to
/// <c>cmd.exe</c>, which would treat <c>&amp;</c>, <c>|</c>, <c>%</c> and the
/// rest in its arguments as syntax.
/// </para>
/// Pure (no I/O) so the rules are unit-testable without a GUI.
/// </summary>
public static class PaneCommandPolicy
{
    /// <summary>
    /// The prefix the surface reads as "the rest is an argv"
    /// (libghostty's <c>Command.fromHost</c>).
    /// </summary>
    public const string SurfaceArgvPrefix = "direct:";

    /// <summary>
    /// A new pane nobody picked a profile for. <paramref name="defaultProfile"/>
    /// is the registry's default (null for none); it stands when
    /// <paramref name="defaultProfileSet"/> (an explicit <c>default-profile</c>
    /// beats <c>command</c>) or when no command is configured. Otherwise the
    /// pane runs the configured command under an identity named after it:
    /// the command replaced the default, so wearing the default profile's
    /// name and icon would label the pane as something it is not.
    /// </summary>
    /// <remarks>
    /// One gap, by design: with <c>default-profile</c> set but no profile
    /// loaded at all (none declared and discovery not finished), this
    /// returns null, and a pane with no snapshot runs libghostty's own
    /// <c>command</c>, which is the configured one when it is set. Picking
    /// anything else would mean inventing a shell the user did not name.
    /// </remarks>
    public static ProfileSnapshot? ImplicitDefault(
        ProfileSnapshot? defaultProfile,
        ConfiguredCommand? configured,
        bool defaultProfileSet,
        string? workingDirectory = null)
    {
        if (!defaultProfileSet
            && configured is { } c
            && !string.IsNullOrWhiteSpace(c.Text))
        {
            return CommandSnapshot(
                c.Text.Trim(), c.IsArgv, PaneCommandOrigin.ConfiguredCommand, workingDirectory);
        }

        return WithWorkingDirectory(defaultProfile, workingDirectory);
    }

    /// <summary>
    /// The configured command when it is what new panes run (set, and no
    /// <c>default-profile</c>), else null. For the UI that names what a new
    /// tab opens: when this is non-null, no profile is "the default".
    /// </summary>
    public static string? CommandInEffect(ConfiguredCommand? configured, bool defaultProfileSet)
        => !defaultProfileSet && configured is { } c && !string.IsNullOrWhiteSpace(c.Text)
            ? c.Text.Trim()
            : null;

    /// <summary>
    /// The first pane of the window a launch opens when the launch named no
    /// profile. <paramref name="launchArgv"/> (the <c>-e</c> command, rendered
    /// by <see cref="SingleInstance.LaunchCommand.FromArgs"/>) wins and keeps
    /// the default profile's identity, the way <c>wt -- cmd</c> does; with no
    /// profile at all it gets an identity of its own, because a null
    /// snapshot would hand the choice back to libghostty. Without <c>-e</c>
    /// this is <see cref="ImplicitDefault"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="initialCommand"/> is the config's <c>initial-command</c>,
    /// passed only for a cold launch that restored nothing: it then gets the
    /// pane <c>-e</c> would, and <c>-e</c> wins when both are set. libghostty
    /// no longer applies it on Windows, because the host owns the first pane.
    /// </remarks>
    public static ProfileSnapshot? LaunchFirstPane(
        ProfileSnapshot? defaultProfile,
        string? launchArgv,
        ConfiguredCommand? configured,
        bool defaultProfileSet,
        string? workingDirectory,
        ConfiguredCommand? initialCommand = null)
    {
        if (!string.IsNullOrWhiteSpace(launchArgv))
            return ApplyLaunchCommand(defaultProfile, launchArgv, workingDirectory);

        if (initialCommand is { } ic && !string.IsNullOrWhiteSpace(ic.Text))
            return ApplyLaunchCommand(defaultProfile, ic.Text.Trim(), ic.IsArgv, workingDirectory);

        return ImplicitDefault(defaultProfile, configured, defaultProfileSet, workingDirectory);
    }

    /// <summary>
    /// <paramref name="snapshot"/> (a profile the launch named, or null)
    /// running the <c>-e</c> command <paramref name="launchArgv"/>, or the
    /// snapshot unchanged when there is none.
    /// </summary>
    public static ProfileSnapshot? ApplyLaunchCommand(
        ProfileSnapshot? snapshot,
        string? launchArgv,
        string? workingDirectory)
        => ApplyLaunchCommand(snapshot, launchArgv, isArgv: true, workingDirectory);

    /// <summary>
    /// <see cref="ApplyLaunchCommand(ProfileSnapshot?, string?, string?)"/>
    /// for a launch command that may be a shell string: <c>initial-command</c>
    /// written without <c>direct:</c> is one, as the user wrote it.
    /// </summary>
    public static ProfileSnapshot? ApplyLaunchCommand(
        ProfileSnapshot? snapshot,
        string? command,
        bool isArgv,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command))
            return WithWorkingDirectory(snapshot, workingDirectory);

        if (snapshot is null)
            return CommandSnapshot(command, isArgv, PaneCommandOrigin.LaunchCommand, workingDirectory);

        return WithWorkingDirectory(snapshot, workingDirectory) with
        {
            ResolvedCommand = command,
            CommandIsArgv = isArgv,
            CommandOrigin = PaneCommandOrigin.LaunchCommand,
        };
    }

    /// <summary>
    /// What a pane opened from <paramref name="source"/> (a split) runs: the
    /// same profile when the source runs a profile. A source that runs
    /// something nobody picked gets <paramref name="implicitDefault"/>,
    /// asked afresh: <c>-e</c> belongs to its launch's first pane only, and
    /// a <c>command</c> pane's split follows the configuration as it is now,
    /// so after a reload it runs what Ctrl+T runs, not the old command.
    /// </summary>
    public static ProfileSnapshot? Inherit(
        ProfileSnapshot? source,
        Func<ProfileSnapshot?> implicitDefault)
    {
        ArgumentNullException.ThrowIfNull(implicitDefault);
        return source is { CommandOrigin: PaneCommandOrigin.LaunchCommand or PaneCommandOrigin.ConfiguredCommand }
            ? implicitDefault()
            : source;
    }

    /// <summary>
    /// The string the surface is handed for <paramref name="snapshot"/>'s
    /// command: the command itself, or, for an argv, the command behind
    /// <see cref="SurfaceArgvPrefix"/> so libghostty splits it and runs it
    /// directly.
    /// </summary>
    /// <remarks>
    /// The prefix is read in band, so a profile whose command line itself
    /// begins with <c>direct:</c> is also run as an argv, split by the
    /// Windows rules, rather than as a shell string. That matches what
    /// <c>direct:</c> means in the config, and it is the safe direction:
    /// the command never reaches <c>cmd.exe</c>.
    /// </remarks>
    public static string SurfaceCommand(ProfileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.CommandIsArgv
            ? SurfaceArgvPrefix + snapshot.ResolvedCommand
            : snapshot.ResolvedCommand;
    }

    private static ProfileSnapshot? WithWorkingDirectory(ProfileSnapshot? snapshot, string? workingDirectory)
        => snapshot is not null && !string.IsNullOrEmpty(workingDirectory)
            ? snapshot with { WorkingDirectory = workingDirectory }
            : snapshot;

    private static ProfileSnapshot CommandSnapshot(
        string command, bool isArgv, PaneCommandOrigin origin, string? workingDirectory)
        => new(
            ProfileId: "",
            Version: 0,
            ResolvedCommand: command,
            WorkingDirectory: string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            DisplayName: DisplayNameFor(command),
            Icon: IconFor(command),
            Visuals: EffectiveVisualOverrides.Empty,
            CommandIsArgv: isArgv,
            CommandOrigin: origin);

    // The same brand glyph a profile running this program would get.
    private static IconSpec IconFor(string command)
        => ProfileOrderResolver.CommandBasename(command) is { } exe
           && ProcessIconTable.TryMap(exe, command) is { } mapped
            ? mapped
            : new IconSpec.BundledKey("default");

    // The program the command runs, without its extension, which is what a
    // tab would show once the process reports its title anyway.
    private static string DisplayNameFor(string command)
    {
        var exe = ProfileOrderResolver.CommandBasename(command);
        return exe is null ? command.Trim() : Path.GetFileNameWithoutExtension(exe);
    }
}
