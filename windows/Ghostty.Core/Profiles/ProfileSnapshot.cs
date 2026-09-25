namespace Ghostty.Core.Profiles;

/// <summary>
/// Per-tab snapshot of a profile, taken at <see cref="Version"/>.
/// Snapshots are immutable; re-resolution produces a new instance.
/// Held by TabModel so that removed/renamed profiles do not orphan
/// open tabs.
/// </summary>
/// <param name="CommandIsArgv">
/// <see cref="ResolvedCommand"/> is an argv quoted by the Windows
/// command-line rules, not a shell string: the surface must split it and run
/// it directly, never through <c>cmd.exe</c>. Set for <c>-e</c> and for a
/// <c>direct:</c> configured command (<see cref="PaneCommandPolicy"/>).
/// </param>
/// <param name="CommandOrigin">
/// Where <see cref="ResolvedCommand"/> came from, which decides what a pane
/// opened from this one inherits (<see cref="PaneCommandPolicy"/>).
/// </param>
public sealed record ProfileSnapshot(
    string ProfileId,
    long Version,
    string ResolvedCommand,
    string? WorkingDirectory,
    string DisplayName,
    IconSpec Icon,
    EffectiveVisualOverrides Visuals,
    bool TabIconTracksForeground = true,
    bool CommandIsArgv = false,
    PaneCommandOrigin CommandOrigin = PaneCommandOrigin.Profile);

/// <summary>Where a pane's command came from.</summary>
public enum PaneCommandOrigin
{
    /// <summary>The profile's own command.</summary>
    Profile,

    /// <summary><c>-e</c>: that launch's first pane only.</summary>
    LaunchCommand,

    /// <summary>The configured <c>command</c> key.</summary>
    ConfiguredCommand,
}
