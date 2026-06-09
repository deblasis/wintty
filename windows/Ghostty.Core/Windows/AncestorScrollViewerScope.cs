namespace Ghostty.Core.Windows;

/// <summary>
/// Decides which ScrollViewers a nearest-first ancestor walk should
/// neuter (IsTabStop=false) to kill the click-focus storm (#159) without
/// breaking legitimate descendant ScrollViewers (#160).
///
/// The walk visits the element's parent first (index 0) and farther
/// ancestors at larger indices. The app content root (Window.Content)
/// appears at <c>rootIndex</c>. The parasitic framework-injected
/// ScrollViewer is an ancestor of the app root (index ≥ rootIndex);
/// legitimate ScrollViewers (settings panes, tab strips) are descendants
/// of the app root (index &lt; rootIndex). When the app root is not found
/// (<c>rootIndex &lt; 0</c>) the original single-pane behaviour is kept:
/// every ScrollViewer is in scope.
///
/// Pure (no WinUI) so it stays unit-testable in Ghostty.Core; the WinUI
/// walk in TerminalControl supplies the indices.
/// </summary>
public static class AncestorScrollViewerScope
{
    public static bool InScope(int index, int rootIndex) =>
        rootIndex < 0 || index >= rootIndex;
}
