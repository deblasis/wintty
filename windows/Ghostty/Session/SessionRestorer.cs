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
    // The saved tab paired with the model built from it, in saved order.
    // Group membership is a saved TAB field (GroupId), and BuildTab skips
    // tabs whose tree would not rebuild, so the pairing -- not list
    // position -- is what maps members back onto their group.
    private readonly List<(TabSession Source, TabModel Tab)> _built = new();

    public SessionRestorer(PaneHostFactory factory, IProfileRegistry? registry)
    {
        _factory = factory;
        _registry = registry;
    }

    public List<TabModel> BuildTabs(WindowSession window)
    {
        var result = new List<TabModel>();
        _built.Clear();
        foreach (var tabDto in window.Tabs)
        {
            if (BuildTab(tabDto) is { } tab)
            {
                result.Add(tab);
                _built.Add((tabDto, tab));
            }
        }
        return result;
    }

    /// <summary>
    /// Rebuild the saved groups into <paramref name="manager"/> through
    /// <see cref="TabManager.RestoreGroup"/>: the saved id, title, color,
    /// and collapse bit come back exactly, and saved membership follows
    /// each tab's GroupId. Must run AFTER every tab from the paired
    /// <see cref="BuildTabs"/> call is in the manager -- the gather is a
    /// manager mutation and needs the full membership present. Restore
    /// never goes through JoinGroup: that op auto-expands on a join, and
    /// the saved collapse bit must survive the restore untouched.
    /// A saved GroupId with no matching <c>Groups</c> entry restores
    /// ungrouped; a group none of whose members rebuilt is never
    /// registered.
    /// </summary>
    public void RestoreGroups(TabManager manager, WindowSession window)
    {
        foreach (var groupDto in window.Groups)
        {
            var members = new List<TabModel>();
            foreach (var (source, tab) in _built)
                if (source.GroupId == groupDto.Id)
                    members.Add(tab);
            manager.RestoreGroup(groupDto.Id, groupDto.Title, groupDto.Color,
                groupDto.Collapsed, members);
        }
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

        // Pin flag first, order later: the flag is written directly (the
        // property's documented contract) and the manager's Normalize
        // folds each pinned tab to the prefix end as it is seeded, in
        // saved order -- so the prefix comes back in the saved relative
        // order without a walk of SetPinned calls.
        tab.IsPinned = tabDto.IsPinned;

        return tab;
    }
}
