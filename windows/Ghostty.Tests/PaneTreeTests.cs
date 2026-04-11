using System.Linq;
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

    [Fact]
    public void FindParent_NodeNotInTree_ReturnsNull()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, a, b);
        var outsider = new LeafPane();

        Assert.Null(PaneTree.FindParent(root, outsider));
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
    public void Close_ParentIsRoot_ClosingChild2_SiblingBecomesRoot()
    {
        var sibling = new LeafPane();
        var target = new LeafPane();
        var root = new SplitPane(PaneOrientation.Horizontal, sibling, target);

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

    [Fact]
    public void Close_ThreeLevelsDeep_CollapsesCorrectly()
    {
        // root = V(a, H(V(target, b), c))
        // closing target should leave root = V(a, H(b, c)).
        var a = new LeafPane();
        var target = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var deepSplit = new SplitPane(PaneOrientation.Vertical, target, b);
        var mid = new SplitPane(PaneOrientation.Horizontal, deepSplit, c);
        var root = new SplitPane(PaneOrientation.Vertical, a, mid);

        var newRoot = PaneTree.Close(root, target);

        Assert.Same(root, newRoot);
        Assert.Same(a, root.Child1);
        var midAfter = Assert.IsType<SplitPane>(root.Child2);
        Assert.Same(b, midAfter.Child1);
        Assert.Same(c, midAfter.Child2);
    }

    [Fact]
    public void Close_TargetNotInTree_ReturnsOriginalRoot()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, a, b);
        var outsider = new LeafPane();

        var newRoot = PaneTree.Close(root, outsider);

        Assert.Same(root, newRoot);
    }

    [Fact]
    public void Close_LeavesAfterClose_ReflectsCollapsedTree()
    {
        // root = V(a, H(target, c)) -> after close -> V(a, c)
        var a = new LeafPane();
        var target = new LeafPane();
        var c = new LeafPane();
        var inner = new SplitPane(PaneOrientation.Horizontal, target, c);
        var root = new SplitPane(PaneOrientation.Vertical, a, inner);

        var newRoot = PaneTree.Close(root, target)!;

        Assert.Equal(new[] { a, c }, PaneTree.Leaves(newRoot).ToArray());
    }

    [Fact]
    public void Close_ThenSplit_RoundTrip()
    {
        // Start with V(a, b), close a -> b is root.
        // Split b -> V(b, c) is new root.
        var a = new LeafPane();
        var b = new LeafPane();
        var root = new SplitPane(PaneOrientation.Vertical, a, b);

        var afterClose = PaneTree.Close(root, a)!;
        Assert.Same(b, afterClose);

        var c = new LeafPane();
        var afterSplit = PaneTree.Split(afterClose, b, c, PaneOrientation.Horizontal);

        var split = Assert.IsType<SplitPane>(afterSplit);
        Assert.Same(b, split.Child1);
        Assert.Same(c, split.Child2);
        Assert.Equal(new[] { b, c }, PaneTree.Leaves(afterSplit).ToArray());
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

    [Fact]
    public void Leaves_Count_MatchesManualCount()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var root = new SplitPane(
            PaneOrientation.Vertical, a,
            new SplitPane(PaneOrientation.Horizontal, b, c));

        Assert.Equal(3, PaneTree.Leaves(root).Count());
    }

    // Equalize ---------------------------------------------------------

    [Fact]
    public void Equalize_SingleLeaf_NoOp()
    {
        var leaf = new LeafPane();
        PaneTree.Equalize(leaf); // should not throw
    }

    [Fact]
    public void Equalize_ResetsRatiosToHalf()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var inner = new SplitPane(PaneOrientation.Horizontal, b, c, ratio: 0.3);
        var root = new SplitPane(PaneOrientation.Vertical, a, inner) { Ratio = 0.7 };

        PaneTree.Equalize(root);

        Assert.Equal(0.5, root.Ratio);
        Assert.Equal(0.5, inner.Ratio);
    }

    [Fact]
    public void Equalize_DeepTree_ResetsAllLevels()
    {
        var a = new LeafPane();
        var b = new LeafPane();
        var c = new LeafPane();
        var d = new LeafPane();
        var deep = new SplitPane(PaneOrientation.Vertical, c, d, ratio: 0.2);
        var mid = new SplitPane(PaneOrientation.Horizontal, b, deep) { Ratio = 0.8 };
        var root = new SplitPane(PaneOrientation.Vertical, a, mid) { Ratio = 0.6 };

        PaneTree.Equalize(root);

        Assert.Equal(0.5, root.Ratio);
        Assert.Equal(0.5, mid.Ratio);
        Assert.Equal(0.5, deep.Ratio);
    }
}
