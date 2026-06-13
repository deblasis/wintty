using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;
using Ghostty.Core.Tabs;
using Ghostty.Tabs;

namespace Ghostty.Session;

/// <summary>
/// Reconstructs <see cref="TabModel"/>s (with tree-seeded pane hosts) from
/// a persisted <see cref="WindowSession"/>. Profiles are re-resolved by id
/// so edits take effect; a removed profile falls back to the saved command.
/// </summary>
internal sealed class SessionRestorer
{
    private readonly PaneHostFactory _factory;
    private readonly IProfileRegistry? _registry;

    public SessionRestorer(PaneHostFactory factory, IProfileRegistry? registry)
    {
        _factory = factory;
        _registry = registry;
    }

    public List<TabModel> BuildTabs(WindowSession window)
    {
        var result = new List<TabModel>();
        foreach (var tabDto in window.Tabs)
        {
            if (tabDto.Tree is null) continue;

            // Rebuild the structure; each leaf re-resolves its own profile.
            var root = SessionTree.RebuildTree(tabDto.Tree,
                leaf => new LeafPane { Snapshot = ResolveSnapshot(leaf) });

            var active = SessionTree.Resolve(root, tabDto.ActiveLeafPath) as LeafPane
                         ?? PaneTree.FirstLeaf(root);
            var zoomed = tabDto.ZoomedLeafPath is { } zp
                ? SessionTree.Resolve(root, zp) as LeafPane
                : null;

            var host = _factory.CreateFromTree(root, active, zoomed);
            var tab = new TabModel(host) { ProfileId = tabDto.ProfileId };

            // The tab's display snapshot: re-resolve the tab's own profile id.
            var tabSnap = ResolveById(tabDto.ProfileId);
            if (tabSnap is not null)
                tab.AttachProfileSnapshot(tabSnap);
            if (tabDto.UserTitle is not null)
                tab.UserOverrideTitle = tabDto.UserTitle;

            result.Add(tab);
        }
        return result;
    }

    private ProfileSnapshot? ResolveSnapshot(LeafDto leaf)
    {
        var byId = ResolveById(leaf.ProfileId);
        if (byId is not null) return byId;
        if (leaf.Fallback is { } fb)
            return new ProfileSnapshot(
                ProfileId: leaf.ProfileId ?? "",
                Version: 0,
                ResolvedCommand: fb.ResolvedCommand,
                WorkingDirectory: fb.WorkingDirectory,
                DisplayName: fb.DisplayName,
                Icon: new IconSpec.BundledKey("default"),
                Visuals: EffectiveVisualOverrides.Empty);
        return null; // legacy no-profile leaf: host spawns the default shell
    }

    private ProfileSnapshot? ResolveById(string? profileId)
    {
        if (_registry is null || profileId is null) return null;
        var resolved = _registry.Resolve(profileId)
            ?? (_registry.DefaultProfileId is { } d ? _registry.Resolve(d) : null);
        return resolved is null ? null : ProfileSnapshotStore.From(resolved, _registry.Version);
    }
}
