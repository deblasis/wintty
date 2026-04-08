using Ghostty.Controls;

namespace Ghostty.Panes;

/// <summary>
/// Orientation of a <see cref="SplitPane"/>.
///
/// Naming follows Windows Terminal convention:
///   - Vertical = a vertical splitter line, panes side-by-side (left | right)
///   - Horizontal = a horizontal splitter line, panes stacked (top / bottom)
///
/// This is the opposite of WinUI's StackPanel.Orientation, so resist the
/// reflex to alias one to the other.
/// </summary>
internal enum PaneOrientation
{
    Vertical,   // splitter line is vertical, panes are left/right
    Horizontal, // splitter line is horizontal, panes are top/bottom
}

/// <summary>
/// A node in the pane tree. Either a leaf (one terminal) or a split
/// (orientation + two child subtrees + ratio). The tree is owned by
/// <see cref="PaneHost"/> and mutated through <see cref="PaneTree"/>.
///
/// Nodes carry no parent back-pointer. Parent lookup is done by walking
/// from the root via <see cref="PaneTree.FindParent"/>. Trees are tiny
/// (realistically &lt; 20 leaves) and the alternative - keeping back
/// pointers in sync on every mutation - is a bug magnet.
/// </summary>
internal abstract class PaneNode
{
}

/// <summary>
/// A leaf pane. Wraps exactly one <see cref="TerminalControl"/>, which
/// owns its own libghostty surface. The control is created once and
/// reused across tree rebuilds; only its parent <see cref="Microsoft.UI.Xaml.Controls.Grid"/>
/// changes.
/// </summary>
internal sealed class LeafPane : PaneNode
{
    public TerminalControl Terminal { get; }

    public LeafPane(TerminalControl terminal)
    {
        Terminal = terminal;
    }
}

/// <summary>
/// A split node. Has two children and a ratio in [0, 1] that controls
/// how the available space is divided between them.
///
/// <see cref="Ratio"/> is mutable so the splitter drag can update it
/// without rebuilding the subtree - only the parent Grid's row/column
/// definitions need to be re-applied.
/// </summary>
internal sealed class SplitPane : PaneNode
{
    public PaneOrientation Orientation { get; }
    public PaneNode Child1 { get; set; }
    public PaneNode Child2 { get; set; }
    public double Ratio { get; set; }

    public SplitPane(
        PaneOrientation orientation,
        PaneNode child1,
        PaneNode child2,
        double ratio = 0.5)
    {
        Orientation = orientation;
        Child1 = child1;
        Child2 = child2;
        Ratio = ratio;
    }
}
