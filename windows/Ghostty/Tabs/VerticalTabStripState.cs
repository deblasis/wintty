namespace Ghostty.Tabs;

/// <summary>
/// State of the vertical tab strip's collapse/expand behavior.
/// Legal transitions:
///
///   Collapsed -> PinnedExpanded     (user clicks the chevron while collapsed)
///   PinnedExpanded -> Collapsed     (user clicks the chevron while expanded)
///   Collapsed -> HoverExpanding     (pointer enters strip for >200ms,
///                                    only when hover-expand enabled and not pinned)
///   HoverExpanding -> HoverExpanded (animation completes)
///   HoverExpanding -> Collapsed     (pointer leaves before animation completes)
///   HoverExpanded -> HoverCollapsing(pointer leaves expanded strip for >400ms)
///   HoverCollapsing -> Collapsed    (collapse animation completes)
///   HoverExpanded -> PinnedExpanded (user clicks chevron while hover-expanded;
///                                    no re-tween, just flip state and show splitter)
///
/// PinnedExpanded is the only state in which the drag handle is
/// visible and hit-testable. The hover paths render the strip as an
/// overlay (Canvas.ZIndex &gt; 0) so the terminal column does not
/// reflow.
/// </summary>
internal enum VerticalTabStripState
{
    Collapsed,
    PinnedExpanded,
    HoverExpanding,
    HoverExpanded,
    HoverCollapsing,
}
