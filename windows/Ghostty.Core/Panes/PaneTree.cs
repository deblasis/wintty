using System;
using System.Collections.Generic;

namespace Ghostty.Core.Panes;

/// <summary>
/// Pure tree operations on <see cref="PaneNode"/>. Visitor helpers and
/// the two structural mutations (<see cref="Split"/>, <see cref="Close"/>)
/// that return new roots.
///
/// All methods are O(n) over leaves. n is small; this is fine and beats
/// the bookkeeping cost of parent back-pointers.
///
/// These functions never touch UI. The renderer (<see cref="PaneHost"/>)
/// reads the resulting tree and updates the visual. Keeping the model
/// pure makes it trivial to unit-test in the future without a XAML host
/// and matches how macOS's SplitTree is structured.
/// </summary>
internal static class PaneTree
{
    /// <summary>
    /// Enumerate every leaf in the subtree rooted at <paramref name="root"/>
    /// in left-to-right (Child1-first) order.
    /// </summary>
    public static IEnumerable<LeafPane> Leaves(PaneNode root)
    {
        switch (root)
        {
            case LeafPane leaf:
                yield return leaf;
                break;
            case SplitPane split:
                foreach (var l in Leaves(split.Child1)) yield return l;
                foreach (var l in Leaves(split.Child2)) yield return l;
                break;
        }
    }

    /// <summary>
    /// Return the leftmost (Child1-first) leaf reachable from
    /// <paramref name="root"/>. Used to pick the focus target after a
    /// pane closes.
    /// </summary>
    public static LeafPane FirstLeaf(PaneNode root)
    {
        var node = root;
        while (node is SplitPane split) node = split.Child1;
        return (LeafPane)node;
    }

    /// <summary>
    /// Find the parent of <paramref name="target"/> by walking from
    /// <paramref name="root"/>. Returns null if <paramref name="target"/>
    /// is the root itself.
    /// </summary>
    public static (SplitPane Parent, bool TargetIsChild1)? FindParent(
        PaneNode root,
        PaneNode target)
    {
        if (ReferenceEquals(root, target)) return null;
        return FindParentInner(root, target);

        static (SplitPane, bool)? FindParentInner(PaneNode node, PaneNode target)
        {
            if (node is not SplitPane split) return null;
            if (ReferenceEquals(split.Child1, target)) return (split, true);
            if (ReferenceEquals(split.Child2, target)) return (split, false);
            return FindParentInner(split.Child1, target)
                ?? FindParentInner(split.Child2, target);
        }
    }

    /// <summary>
    /// Replace <paramref name="target"/> in <paramref name="root"/> with
    /// a new <see cref="SplitPane"/> containing the target as Child1 and
    /// <paramref name="newLeaf"/> as Child2 with the given orientation.
    /// Returns the new root (which equals <paramref name="root"/> unless
    /// <paramref name="target"/> WAS the root, in which case the returned
    /// root is the new SplitPane).
    ///
    /// Caller is responsible for creating <paramref name="newLeaf"/>'s
    /// libghostty surface before calling.
    /// </summary>
    public static PaneNode Split(
        PaneNode root,
        LeafPane target,
        LeafPane newLeaf,
        PaneOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(newLeaf);

        var newSplit = new SplitPane(orientation, target, newLeaf, ratio: 0.5);

        var parent = FindParent(root, target);
        if (parent is null)
        {
            // target was the root.
            return newSplit;
        }

        var (split, targetIsChild1) = parent.Value;
        if (targetIsChild1) split.Child1 = newSplit;
        else split.Child2 = newSplit;
        return root;
    }

    /// <summary>
    /// Remove <paramref name="target"/> from <paramref name="root"/>,
    /// collapsing its parent split (the sibling subtree replaces the
    /// parent).
    ///
    /// Returns:
    ///   - the new root (sibling) if <paramref name="target"/>'s parent
    ///     was the root,
    ///   - the same <paramref name="root"/> with the parent collapsed
    ///     in place otherwise,
    ///   - null if <paramref name="target"/> WAS the root (caller should
    ///     close the window).
    ///
    /// Caller is responsible for freeing <paramref name="target"/>'s
    /// libghostty surface before or after calling.
    /// </summary>
    public static PaneNode? Close(PaneNode root, LeafPane target)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);

        if (ReferenceEquals(root, target)) return null;

        var parent = FindParent(root, target);
        if (parent is null)
        {
            // target is not in this tree at all - defensive no-op.
            return root;
        }

        var (parentSplit, targetIsChild1) = parent.Value;
        var sibling = targetIsChild1 ? parentSplit.Child2 : parentSplit.Child1;

        var grandparent = FindParent(root, parentSplit);
        if (grandparent is null)
        {
            // parentSplit was the root - sibling becomes the new root.
            return sibling;
        }

        var (gpSplit, parentIsChild1) = grandparent.Value;
        if (parentIsChild1) gpSplit.Child1 = sibling;
        else gpSplit.Child2 = sibling;
        return root;
    }
}
