using Ghostty.Core.Panes;

namespace Ghostty.Core.Session;

/// <summary>
/// Builds the serializable per-tab record from live pane-tree references.
/// Pure: the app layer supplies the root/active/zoomed leaves and the
/// tab's profile id + title.
/// </summary>
internal static class SessionCapture
{
    public static TabSession CaptureTab(
        PaneNode root,
        LeafPane activeLeaf,
        LeafPane? zoomed,
        string? profileId,
        string? userTitle) => new()
        {
            ProfileId = profileId,
            UserTitle = userTitle,
            Tree = SessionTree.CaptureTree(root),
            ActiveLeafPath = SessionTree.PathOf(root, activeLeaf),
            ZoomedLeafPath = zoomed is null ? null : SessionTree.PathOf(root, zoomed),
        };
}
