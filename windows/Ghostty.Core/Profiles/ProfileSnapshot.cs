namespace Ghostty.Core.Profiles;

/// <summary>
/// Per-tab snapshot of a profile, taken at <see cref="Version"/>.
/// Snapshots are immutable; re-resolution produces a new instance.
/// Held by TabModel so that removed/renamed profiles do not orphan
/// open tabs.
/// </summary>
public sealed record ProfileSnapshot(
    string ProfileId,
    long Version,
    string ResolvedCommand,
    string? WorkingDirectory,
    string DisplayName,
    IconSpec Icon,
    EffectiveVisualOverrides Visuals);
