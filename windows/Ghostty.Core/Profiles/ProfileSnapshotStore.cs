using System;
using System.Collections.Generic;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Builds and refreshes <see cref="ProfileSnapshot"/> instances for
/// open tabs. Pure logic: the per-tab storage of the snapshot itself
/// belongs to TabModel (PR 3). This type only knows how to derive
/// snapshots from the current resolved profile list.
/// </summary>
public static class ProfileSnapshotStore
{
    /// <summary>
    /// First-time resolution for a freshly-opened tab. Returns null
    /// if the requested profile is not in the resolved list (caller
    /// is expected to fall back to the default profile in that case).
    /// </summary>
    public static ProfileSnapshot? Resolve(
        string profileId,
        IReadOnlyList<ResolvedProfile> resolvedProfiles,
        long version)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(resolvedProfiles);

        var profile = FindById(resolvedProfiles, profileId);
        if (profile is null) return null;
        return BuildSnapshot(profile, version);
    }

    /// <summary>
    /// Refresh an existing snapshot against a new resolved list.
    /// Graceful path: if the profile is no longer in the list (removed
    /// or renamed), returns the existing snapshot unchanged. The open
    /// tab keeps its visual + identity state.
    /// </summary>
    public static ProfileSnapshot Refresh(
        ProfileSnapshot existing,
        IReadOnlyList<ResolvedProfile> resolvedProfiles,
        long newVersion)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(resolvedProfiles);

        var profile = FindById(resolvedProfiles, existing.ProfileId);
        if (profile is null) return existing;
        return BuildSnapshot(profile, newVersion);
    }

    private static ResolvedProfile? FindById(
        IReadOnlyList<ResolvedProfile> resolvedProfiles,
        string id)
    {
        foreach (var p in resolvedProfiles)
            if (p.Id == id) return p;
        return null;
    }

    private static ProfileSnapshot BuildSnapshot(ResolvedProfile profile, long version)
        => new(
            ProfileId: profile.Id,
            Version: version,
            ResolvedCommand: profile.Command,
            WorkingDirectory: profile.WorkingDirectory,
            DisplayName: profile.Name,
            Icon: profile.Icon,
            Visuals: profile.Visuals);
}
