using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests;

/// <summary>
/// Unit tests for <see cref="PaneTree"/>. Exercises the pure tree
/// operations without a WinUI 3 host; <see cref="LeafPane"/>'s
/// internal test constructor leaves Terminal null because
/// <see cref="PaneTree"/> compares leaves by reference only.
/// </summary>
public sealed class PaneTreeTests
{
    // FindParent -------------------------------------------------------

    [Fact]
    public void FindParent_RootIsTarget_ReturnsNull()
    {
        var root = new LeafPane();
        Assert.Null(PaneTree.FindParent(root, root));
    }

    [Fact]
    public void FindParent_DirectChild_ReturnsSplitAndChildFlag()
    {
        var left = new LeafPane();
        var right = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, left, right);

        var leftParent = PaneTree.FindParent(root, left);
        var rightParent = PaneTree.FindParent(root, right);

        Assert.NotNull(leftParent);
        Assert.Same(root, leftParent!.Value.Parent);
        Assert.True(leftParent.Value.TargetIsChild1);

        Assert.NotNull(rightParent);
        Assert.Same(root, rightParent!.Value.Parent);
        Assert.False(rightParent.Value.TargetIsChild1);
    }

    [Fact]
    public void FindParent_NestedChild_WalksTree()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var inner = new SplitPane(PaneOrientation.Horizontal, b, c);
        var root = new SplitPane(PaneOrientation.Vertical, a, inner);

        var parentOfC = PaneTree.FindParent(root, c);

        Assert.NotNull(parentOfC);
        Assert.Same(inner, parentOfC!.Value.Parent);
        Assert.False(parentOfC.Value.TargetIsChild1);
    }

    // FirstLeaf --------------------------------------------------------

    [Fact]
    public void FirstLeaf_SingleLeaf_ReturnsItself()
    {
        var leaf = new LeafPane();
        Assert.Same(leaf, PaneTree.FirstLeaf(leaf));
    }

    [Fact]
    public void FirstLeaf_WalksChild1Chain()
    {
        var deepest = new LeafPane();
        var right = new LeafPane();
        var inner = new SplitPane(PaneOrientation.Vertical, deepest, new LeafPane());
        var root = new SplitPane(PaneOrientation.Horizontal, inner, right);

        Assert.Same(deepest, PaneTree.FirstLeaf(root));
    }

    // Split ------------------------------------------------------------

    [Fact]
    public void Split_LeafRoot_NewRootIsSplit()
    {
        var target = new LeafPane();
        var added = new LeafPane();

        var newRoot = PaneTree.Split(target, target, added, PaneOrientation.Vertical);

        var split = Assert.IsType<SplitPane>(newRoot);
        Assert.Same(target, split.Child1);
        Assert.Same(added, split.Child2);
        Assert.Equal(PaneOrientation.Vertical, split.Orientation);
        Assert.Equal(0.5, split.Ratio);
    }

    [Fact]
    public void Split_NonRootLeafOnChild1_WrapsInPlace()
    {
        var target = new LeafPane();
        var sibling = new LeafPane();
        var added = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, target, sibling);

        var newRoot = PaneTree.Split(root, target, added, PaneOrientation.Horizontal);

        Assert.Same(root, newRoot);
        var wrapper = Assert.IsType<SplitPane>(root.Child1);
        Assert.Same(target, wrapper.Child1);
        Assert.Same(added, wrapper.Child2);
        Assert.Equal(PaneOrientation.Horizontal, wrapper.Orientation);
        Assert.Same(sibling, root.Child2);
    }

    [Fact]
    public void Split_NonRootLeafOnChild2_WrapsInPlace()
    {
        var sibling = new LeafPane();
        var target = new LeafPane();
        var added = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, sibling, target);

        var newRoot = PaneTree.Split(root, target, added, PaneOrientation.Vertical);

        Assert.Same(root, newRoot);
        Assert.Same(sibling, root.Child1);
        var wrapper = Assert.IsType<SplitPane>(root.Child2);
        Assert.Same(target, wrapper.Child1);
        Assert.Same(added, wrapper.Child2);
    }

    // Close ------------------------------------------------------------

    [Fact]
    public void Close_RootLeaf_ReturnsNull()
    {
        var root = new LeafPane();
        Assert.Null(PaneTree.Close(root, root));
    }

    [Fact]
    public void Close_ParentIsRoot_SiblingBecomesRoot()
    {
        var target = new LeafPane();
        var sibling = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, target, sibling);

        var newRoot = PaneTree.Close(root, target);

        Assert.Same(sibling, newRoot);
    }

    [Fact]
    public void Close_NestedTarget_CollapsesParentInPlace()
    {
        // root = V(a, H(target, c))
        // closing target should leave root = V(a, c).
        var a = new LeafPane();
        var target = new LeafPane();
        var c = new LeafPane();
        var inner = new SplitPane(PaneOrientation.Horizontal, target, c);
        var root = new SplitPane(PaneOrientation.Vertical, a, inner);

        var newRoot = PaneTree.Close(root, target);

        Assert.Same(root, newRoot);
        Assert.Same(a, root.Child1);
        Assert.Same(c, root.Child2);
    }

    [Fact]
    public void Close_NestedTarget_SiblingIsSubtree()
    {
        // root = V(a, H(target, nested(d, e)))
        // closing target leaves root = V(a, nested(d, e)).
        var a = new LeafPane();
        var target = new LeafPane();
        var d = new LeafPane();
        var e = new LeafPane();
        var nested = new SplitPane(PaneOrientation.Vertical, d, e);
        var inner = new SplitPane(PaneOrientation.Horizontal, target, nested);
        var root = new SplitPane(PaneOrientation.Vertical, a, inner);

        var newRoot = PaneTree.Close(root, target);

        Assert.Same(root, newRoot);
        Assert.Same(nested, root.Child2);
        Assert.Same(d, ((SplitPane)root.Child2).Child1);
        Assert.Same(e, ((SplitPane)root.Child2).Child2);
    }

    // Leaves -----------------------------------------------------------

    [Fact]
    public void Leaves_SingleLeaf_YieldsItself()
    {
        var leaf = new LeafPane();
        Assert.Equal(new[] { leaf }, PaneTree.Leaves(leaf));
    }

    [Fact]
    public void Leaves_LeftToRightOrder()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var d = new LeafPane();
        var root = new SplitPane(
            PaneOrientation.Vertical,
            new SplitPane(PaneOrientation.Horizontal, a, b),
            new SplitPane(PaneOrientation.Horizontal, c, d));

        Assert.Equal(new[] { a, b, c, d }, PaneTree.Leaves(root));
    }
}
