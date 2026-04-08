namespace Ghostty.Panes;

// Local stubs that mirror the real types in windows/Ghostty/Panes/PaneNode.cs
// without the TerminalControl reference. PaneTree.cs is compiled directly
// into this test assembly via <Compile Include="..\Ghostty\Panes\PaneTree.cs" />
// and binds to these stubs. Keep the public surface (names, signatures,
// visibility) in sync with the real types when PaneNode.cs changes.

internal enum PaneOrientation
{
    Vertical,
    Horizontal,
}

internal abstract class PaneNode
{
}

internal sealed class LeafPane : PaneNode
{
    // No Terminal field here: PaneTree never dereferences it, and
    // pulling TerminalControl would drag the entire WinUI 3 stack into
    // the test project. Reference equality is all PaneTree needs.
}

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
