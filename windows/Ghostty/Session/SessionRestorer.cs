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
            if (BuildTab(tabDto) is { } tab)
                result.Add(tab);
        }
        return result;
    }

    /// <summary>
    /// Rebuild a single <see cref="TabModel"/> (tree-seeded pane host, fresh
    /// shells) from one persisted <see cref="TabSession"/>, or null if the
    /// snapshot has no tree. Used by <see cref="BuildTabs"/> and by the
    /// same-session reopen-closed-tab path.
    /// </summary>
    public TabModel? BuildTab(TabSession tabDto)
    {
        if (tabDto.Tree is null) return null;

        // Rebuild the structure; each leaf re-resolves its own profile
        // (exact id, else its saved fallback command).
        var root = SessionTree.RebuildTree(tabDto.Tree,
            leaf => new LeafPane { Snapshot = SessionProfileResolver.ResolveLeaf(_registry, leaf) });

        var active = SessionTree.Resolve(root, tabDto.ActiveLeafPath) as LeafPane
                     ?? PaneTree.FirstLeaf(root);
        var zoomed = tabDto.ZoomedLeafPath is { } zp
            ? SessionTree.Resolve(root, zp) as LeafPane
            : null;

        var host = _factory.CreateFromTree(root, active, zoomed);
        var tab = new TabModel(host) { ProfileId = tabDto.ProfileId };

        // The tab's display snapshot: re-resolve the tab's own profile id
        // if it still exists (else the tab keeps its restored title).
        var tabSnap = SessionProfileResolver.ResolveById(_registry, tabDto.ProfileId);
        if (tabSnap is not null)
            tab.AttachProfileSnapshot(tabSnap);
        if (tabDto.UserTitle is not null)
            tab.UserOverrideTitle = tabDto.UserTitle;

        return tab;
    }
}
