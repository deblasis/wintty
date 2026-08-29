using System;
using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Session;

/// <summary>
/// Builds the serializable per-tab record from live pane-tree references.
/// Pure: the app layer supplies the root/active/zoomed leaves and the
/// tab's profile id + title.
/// </summary>
internal static class SessionCapture
{
    // Invariant: activeLeaf and zoomed (when non-null) are leaves OF root.
    // PathOf returns the empty path both for "is root" and "not found", so
    // passing a foreign node would serialize as the root path; callers only
    // ever pass live leaves of this tree, so the empty path always means root.
    //
    // isPinned/groupId ride the debounced session write. The reopen
    // snapshot (CloseTab) keeps the defaults: a reopened tab comes back
    // unpinned and ungrouped. DuplicateTab keeps them too -- its call
    // site re-applies the clone's pin explicitly, and neither path
    // carries group state.
    public static TabSession CaptureTab(
        PaneNode root,
        LeafPane activeLeaf,
        LeafPane? zoomed,
        string? profileId,
        string? userTitle,
        bool isPinned = false,
        Guid? groupId = null) => new()
        {
            ProfileId = profileId,
            UserTitle = userTitle,
            Tree = SessionTree.CaptureTree(root),
            ActiveLeafPath = SessionTree.PathOf(root, activeLeaf),
            ZoomedLeafPath = zoomed is null ? null : SessionTree.PathOf(root, zoomed),
            IsPinned = isPinned,
            GroupId = groupId,
        };

    /// <summary>
    /// The window's group registry as it must come back after a restart:
    /// id, title, color, and the shared collapse bit. Membership is NOT
    /// captured here -- members point at their group by
    /// <see cref="TabSession.GroupId"/>, so the tab list already carries
    /// it and a group whose every member failed to capture restores as
    /// nothing.
    /// </summary>
    public static List<GroupSession> CaptureGroups(IReadOnlyList<TabGroup> groups)
    {
        var captured = new List<GroupSession>(groups.Count);
        foreach (var group in groups)
        {
            captured.Add(new GroupSession
            {
                Id = group.Id,
                Title = group.Title,
                Color = group.Color,
                Collapsed = group.IsCollapsed,
            });
        }
        return captured;
    }
}
