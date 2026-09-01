using Ghostty.Core.Profiles;

namespace Ghostty.Core.Session;

/// <summary>
/// Turns persisted profile ids back into <see cref="ProfileSnapshot"/>s at
/// restore time. Pure given an <see cref="IProfileRegistry"/>, so the
/// fallback policy is unit-testable. Resolution is by exact id only: a
/// profile that no longer exists does NOT silently become the default
/// profile -- a leaf falls back to its saved command instead, so a tab that
/// ran a since-deleted profile re-runs what it was actually running rather
/// than the user's default shell.
/// </summary>
internal static class SessionProfileResolver
{
    /// <summary>
    /// Exact-id resolution: re-resolve a still-existing profile fresh (so
    /// profile edits take effect), or null if the id is unknown/null.
    /// </summary>
    public static ProfileSnapshot? ResolveById(IProfileRegistry? registry, string? profileId)
    {
        if (registry is null || profileId is null) return null;
        var resolved = registry.Resolve(profileId);
        return resolved is null ? null : ProfileSnapshotStore.From(resolved, registry.Version);
    }

    /// <summary>
    /// Cold-start default: the registry's <c>default-profile</c> if it
    /// still resolves, else null (legacy no-profile path -- the host
    /// spawns the platform default shell).
    /// </summary>
    public static ProfileSnapshot? ResolveDefault(IProfileRegistry? registry)
        => ResolveById(registry, registry?.DefaultProfileId);

    /// <summary>
    /// Per-leaf resolution: the leaf's profile if it still exists, else the
    /// saved fallback command, else null (legacy no-profile leaf -- the host
    /// spawns the default shell). A leaf that reported its directory (OSC 7)
    /// is spawned there in preference to any static working-directory: that
    /// substitution is what "same folder" means for duplicate tab and for
    /// restore.
    /// </summary>
    public static ProfileSnapshot? ResolveLeaf(IProfileRegistry? registry, LeafDto leaf)
    {
        var byId = ResolveById(registry, leaf.ProfileId);
        if (byId is not null) return SpawnAtReportedCwd(byId, leaf.Cwd);
        if (leaf.Fallback is { } fb)
            return SpawnAtReportedCwd(new ProfileSnapshot(
                ProfileId: leaf.ProfileId ?? "",
                Version: 0,
                ResolvedCommand: fb.ResolvedCommand,
                WorkingDirectory: fb.WorkingDirectory,
                DisplayName: fb.DisplayName,
                Icon: new IconSpec.BundledKey("default"),
                Visuals: EffectiveVisualOverrides.Empty), leaf.Cwd);
        return null;
    }

    /// <summary>
    /// The pane's last-reported directory outranks the profile's static
    /// one. Null and empty both mean "never reported"; the surface config
    /// treats an empty working-directory as unset, so an empty string must
    /// not replace the profile's value either.
    /// </summary>
    /// <remarks>
    /// A reported directory the shell cannot be trusted to have named is
    /// dropped rather than spawned into, and the profile's own directory
    /// stands -- see <see cref="SpawnCwdPolicy"/> for what that means and
    /// why this is the funnel it guards.
    /// </remarks>
    private static ProfileSnapshot SpawnAtReportedCwd(ProfileSnapshot snap, string? cwd)
        => SpawnCwdPolicy.MaySpawnAt(cwd) ? snap with { WorkingDirectory = cwd } : snap;
}
