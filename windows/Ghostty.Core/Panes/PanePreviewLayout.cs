using System.Collections.Generic;

namespace Ghostty.Core.Panes;

/// <summary>A pane's position within the tile body, normalized to [0,1].</summary>
internal readonly record struct PreviewRect(double X, double Y, double W, double H);

/// <summary>
/// Flattens a pane tree into each leaf's normalized rectangle so the overview
/// tile can render the split geometry. Pure: input tree -> output rects.
/// <see cref="PaneOrientation.Vertical"/> splits side-by-side (Child1 = left
/// <c>Ratio</c>), <see cref="PaneOrientation.Horizontal"/> stacks (Child1 = top).
/// </summary>
internal static class PanePreviewLayout
{
    public static IReadOnlyList<(LeafPane, PreviewRect)> Compute(PaneNode root)
    {
        var result = new List<(LeafPane, PreviewRect)>();
        Walk(root, new PreviewRect(0, 0, 1, 1), result);
        return result;
    }

    private static void Walk(PaneNode node, PreviewRect rect, List<(LeafPane, PreviewRect)> acc)
    {
        switch (node)
        {
            case LeafPane leaf:
                acc.Add((leaf, rect));
                break;
            case SplitPane split when split.Orientation == PaneOrientation.Vertical:
                Walk(split.Child1, new PreviewRect(rect.X, rect.Y, rect.W * split.Ratio, rect.H), acc);
                Walk(split.Child2, new PreviewRect(rect.X + rect.W * split.Ratio, rect.Y, rect.W * (1 - split.Ratio), rect.H), acc);
                break;
            case SplitPane split:
                Walk(split.Child1, new PreviewRect(rect.X, rect.Y, rect.W, rect.H * split.Ratio), acc);
                Walk(split.Child2, new PreviewRect(rect.X, rect.Y + rect.H * split.Ratio, rect.W, rect.H * (1 - split.Ratio)), acc);
                break;
        }
    }
}
