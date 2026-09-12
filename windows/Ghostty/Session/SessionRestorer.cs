using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;
using Ghostty.Core.Tabs;
using Ghostty.Logging;
using Ghostty.Tabs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Session;

/// <summary>
/// Reconstructs <see cref="TabModel"/>s (with tree-seeded pane hosts) from
/// a persisted <see cref="WindowSession"/>. Profiles are re-resolved by id
/// so edits take effect; a removed profile falls back to the saved command,
/// except a leaf whose id resolves to nothing offered and whose saved
/// command cannot be spawned (a withdrawn built-in, or the retired
/// <c>${env:}</c> template), which is dropped rather than spawned dead.
/// </summary>
internal sealed class SessionRestorer
{
    private readonly PaneHostFactory _factory;
    private readonly IProfileRegistry? _registry;
    private readonly ILogger<SessionRestorer> _logger;
    // The saved tab paired with the model built from it, in saved order.
    // Group membership is a saved TAB field (GroupId), and BuildTab skips
    // tabs whose tree would not rebuild, so the pairing -- not list
    // position -- is what maps members back onto their group.
    private readonly List<(TabSession Source, TabModel Tab)> _built = new();

    public SessionRestorer(
        PaneHostFactory factory,
        IProfileRegistry? registry,
        ILogger<SessionRestorer>? logger = null)
    {
        _factory = factory;
        _registry = registry;
        _logger = logger ?? NullLogger<SessionRestorer>.Instance;
    }

    public List<TabModel> BuildTabs(WindowSession window)
    {
        var result = new List<TabModel>();
        _built.Clear();
        var dropped = new List<string>();
        foreach (var tabDto in window.Tabs)
        {
            if (BuildTab(tabDto, dropped) is { } tab)
            {
                result.Add(tab);
                _built.Add((tabDto, tab));
            }
        }

        // One notice per restore naming everything that went, not a line
        // per leaf: a gated preset can take a whole saved window's worth
        // of tabs with it, and per-leaf spam would say the same thing N
        // times. Only the restore pass logs (it owns the logger); the
        // reopen/duplicate entry points below drop silently, and their
        // captures come from live tabs, where a refusal is not a real
        // state.
        if (dropped.Count > 0)
            _logger.LogSessionRestoreDroppedLeaves(dropped.Count, string.Join(", ", dropped));

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
    /// snapshot has no tree -- or if every leaf in it was dropped, in which
    /// case the tab is not restored at all and, never built, is not
    /// re-saved. Used by the same-session reopen-closed-tab path (drops
    /// there are silent: no restore logger is in play).
    /// </summary>
    public TabModel? BuildTab(TabSession tabDto) => BuildTab(tabDto, new List<string>());

    private TabModel? BuildTab(TabSession tabDto, List<string> dropped)
    {
        if (tabDto.Tree is null) return null;

        // Rebuild the structure; each leaf re-resolves its own profile
        // (exact id, else its saved fallback command). A refused leaf is
        // dropped: RebuildTree collapses the splits it empties and hands
        // back null when nothing survives.
        var root = SessionTree.RebuildTree(tabDto.Tree, leaf =>
        {
            if (SessionProfileResolver.ShouldDropLeaf(_registry, leaf))
            {
                dropped.Add(DroppedLeafName(leaf));
                return null;
            }
            return new LeafPane { Snapshot = SessionProfileResolver.ResolveLeaf(_registry, leaf) };
        });
        if (root is null) return null;

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

    /// <summary>
    /// How a dropped leaf is named in the restore notice: the display
    /// name its profile carried when it was saved (the name the user
    /// saw on the tab), falling back to the raw id for saves that
    /// predate a display name.
    /// </summary>
    private static string DroppedLeafName(LeafDto leaf) =>
        !string.IsNullOrEmpty(leaf.Fallback?.DisplayName)
            ? leaf.Fallback!.DisplayName
            : leaf.ProfileId!;
}

internal static partial class SessionRestorerLogExtensions
{
    [LoggerMessage(EventId = LogEvents.Session.RestoreDroppedLeaves,
                   Level = LogLevel.Warning, Message = "Session restore dropped {Count} saved pane(s) whose profile is no longer offered: {Names}")]
    internal static partial void LogSessionRestoreDroppedLeaves(
        this ILogger<SessionRestorer> logger, int count, string names);
}
