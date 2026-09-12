using System;
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
    /// The reserved id namespace for built-in / preset profiles: ids a
    /// shipping composition prepends to the config source (tier overlays
    /// like the Headless SSH preset) rather than anything the user's own
    /// config declares. The registry compares ids without case, so the
    /// prefix match does too; a user override of a built-in id resolves
    /// normally and is never treated as withdrawn.
    /// </summary>
    internal const string BuiltInProfileIdPrefix = "wintty.builtin.";

    /// <summary>
    /// The one template dialect a profile command has shipped
    /// (<c>${env:NAME}</c>, rc.1's Headless SSH preset). Nothing expands
    /// it on any spawn path: the dialect is retired, and the local pane
    /// path hands <see cref="ProfileSnapshot.ResolvedCommand"/> to the OS
    /// verbatim, so a saved leaf still carrying it would spawn the
    /// literal token as an argument.
    /// </summary>
    internal const string EnvTemplateMarker = "${env:";

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
    /// Whether a saved leaf must be dropped at restore instead of
    /// spawned: its profile id resolves to nothing the current registry
    /// offers, AND the fallback that would run in the profile's place
    /// cannot be spawned as saved. Two things make a fallback unspawnable:
    /// <list type="number">
    /// <item>the id sits in the reserved built-in namespace
    /// (<see cref="BuiltInProfileIdPrefix"/>) yet nothing claims it, so
    /// this machine's composition is refusing to offer that built-in --
    /// gated off, or a tier that never shipped it; the saved command is
    /// the built-in's launch line, which the composition just decided
    /// must not launch here</item>
    /// <item>the saved command still carries the retired
    /// <see cref="EnvTemplateMarker"/> dialect, which no spawn path
    /// expands, so the pane would hand the literal template token to the
    /// child process and die on it</item>
    /// </list>
    /// The boundary, deliberately: a leaf whose profile was merely
    /// renamed or deleted is NOT dropped. Its saved command is the
    /// user's own, spawnable as written, so it keeps the fallback
    /// behaviour <see cref="ResolveLeaf"/> documents -- re-run what the
    /// tab was actually running. Both clauses refuse the drop for that
    /// case: an ordinary custom id with an ordinary command is kept
    /// however unknown, and a resolvable id is kept even when its
    /// command carries the dialect (the fresh profile wins; the fallback
    /// is never consulted).
    /// </summary>
    public static bool ShouldDropLeaf(IProfileRegistry? registry, LeafDto leaf)
    {
        // Only the fallback arm is droppable. A resolvable profile id
        // re-resolves fresh, and a legacy no-profile leaf spawns the
        // default shell; neither is a fossil.
        if (leaf.ProfileId is null) return false;
        if (ResolveById(registry, leaf.ProfileId) is not null) return false;

        // A withdrawn built-in: the id sits in the reserved built-in
        // namespace, yet nothing in this composition claims it -- gated
        // off here, or a tier that never shipped it. Its saved command
        // is the built-in's own launch line, which this machine just
        // decided must not launch.
        if (leaf.ProfileId.StartsWith(BuiltInProfileIdPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        // A saved command still in the retired ${env: dialect: nothing
        // expands it, so the pane would hand the literal template token
        // to its child process and die on it.
        return leaf.Fallback?.ResolvedCommand.Contains(EnvTemplateMarker, StringComparison.Ordinal) ?? false;
    }

    /// <summary>
    /// The pane's last-reported directory outranks the profile's static
    /// one. Null and empty both mean "never reported"; the surface config
    /// treats an empty working-directory as unset, so an empty string must
    /// not replace the profile's value either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reported directory the shell cannot be trusted to have named is
    /// dropped rather than spawned into, and the profile's own directory
    /// stands -- see <see cref="SpawnCwdPolicy"/> for what that means and
    /// why this is the funnel it guards.
    /// </para>
    /// <para>
    /// Both tests, the same pair <see cref="Ghostty.Core.Tabs.TabModel"/>
    /// requires before it will put a directory on the clipboard: a host the
    /// spawn policy accepts, AND plain text. The terminal core refuses a
    /// path carrying control characters at the source now, but this path
    /// reaches further back than the core can -- a restored session was
    /// written by whatever build recorded it, including builds with no such
    /// check -- and it is the funnel that reaches CreateProcess. Checking
    /// only the host here meant a directory refused for the clipboard, for
    /// Explorer, for the label and for the tooltip was still handed to a
    /// spawn.
    /// </para>
    /// </remarks>
    private static ProfileSnapshot SpawnAtReportedCwd(ProfileSnapshot snap, string? cwd)
        => cwd is not null && Ghostty.Core.Tabs.TabLabel.IsPlain(cwd) && SpawnCwdPolicy.MaySpawnAt(cwd)
            ? snap with { WorkingDirectory = cwd }
            : snap;
}
