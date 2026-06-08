using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests;

public sealed class PaneTreeCloneTests
{
    [Fact]
    public void Clone_SingleLeaf_ReturnsSameLeafReference()
    {
        var leaf = new LeafPane();
        var clone = PaneTree.Clone(leaf);
        Assert.Same(leaf, clone); // leaf identities are preserved
    }

    [Fact]
    public void Clone_Split_NewSplitNodeButSharedLeaves()
    {
        var l = new LeafPane();
        var r = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, l, r, ratio: 0.3);

        var clone = (SplitPane)PaneTree.Clone(root);

        Assert.NotSame(root, clone);                 // structural node is new
        Assert.Equal(PaneOrientation.Vertical, clone.Orientation);
        Assert.Equal(0.3, clone.Ratio);
        Assert.Same(l, clone.Child1);                // leaves shared
        Assert.Same(r, clone.Child2);
    }

    [Fact]
    public void Clone_IsDeep_NestedSplitsAreNewNodes()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var inner = new SplitPane(PaneOrientation.Horizontal, b, c, ratio: 0.7);
        var root = new SplitPane(PaneOrientation.Vertical, a, inner, ratio: 0.4);

        var clone = (SplitPane)PaneTree.Clone(root);
        var clonedInner = (SplitPane)clone.Child2;

        Assert.NotSame(inner, clonedInner);
        Assert.Equal(0.7, clonedInner.Ratio);
        Assert.Same(b, clonedInner.Child1);
        Assert.Same(c, clonedInner.Child2);
    }

    [Fact]
    public void Clone_MutatingCloneRatio_DoesNotAffectOriginal()
    {
        var root = new SplitPane(PaneOrientation.Vertical, new LeafPane(), new LeafPane(), ratio: 0.5);
        var clone = (SplitPane)PaneTree.Clone(root);
        clone.Ratio = 0.9;
        Assert.Equal(0.5, root.Ratio); // independence
    }
}
