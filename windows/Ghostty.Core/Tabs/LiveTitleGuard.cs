namespace Ghostty.Core.Tabs;

/// <summary>
/// Only the active leaf's surface names its tab. The pane host checks every
/// TitleChanged against its active leaf's terminal before forwarding it, so a
/// title from a background split, or from a pane soft-closed and kept alive
/// for undo, cannot relabel the tab. The guard used to sit in the window's
/// title coordinator, where the same stale callback from a closing tab
/// stamped the remaining tab with the dead tab's last title (e.g. cmd.exe).
/// </summary>
public static class LiveTitleGuard
{
    public static bool Accepts(object? sender, object? activeTerminal)
        => activeTerminal is not null && ReferenceEquals(sender, activeTerminal);
}
