using System;
using System.Collections.Generic;
using System.Linq;

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
    /// Deep-clone the structure of <paramref name="root"/>: every
    /// <see cref="SplitPane"/> becomes a NEW node copying orientation and
    /// ratio, but every <see cref="LeafPane"/> is returned by the SAME
    /// reference. Used to snapshot the tree for undo/redo without aliasing
    /// the live tree's mutable split nodes, while keeping leaf identity so
    /// the visual rebuild reuses the live TerminalControl/surface.
    /// </summary>
    public static PaneNode Clone(PaneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root switch
        {
            SplitPane s => new SplitPane(
                s.Orientation,
                Clone(s.Child1),
                Clone(s.Child2),
                s.Ratio),
            _ => root, // LeafPane: shared by reference
        };
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

    /// <summary>
    /// Return the leaf that follows <paramref name="current"/> in
    /// left-to-right depth-first traversal order, wrapping back to
    /// the first leaf when called on the last one. Returns null if
    /// <paramref name="current"/> is not in the tree or the tree
    /// contains a single leaf.
    ///
    /// Tree-order navigation complements spatial focus (Alt+Arrows):
    /// it always advances even when no leaf lies in any spatial
    /// direction. Matches Ghostty's goto_split:next semantics.
    /// </summary>
    public static LeafPane? NextLeafInOrder(PaneNode root, LeafPane current)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(current);

        var ordered = Leaves(root).ToList();
        if (ordered.Count <= 1) return null;

        var idx = ordered.IndexOf(current);
        if (idx < 0) return null;
        return ordered[(idx + 1) % ordered.Count];
    }

    /// <summary>
    /// Return the leaf that precedes <paramref name="current"/> in
    /// left-to-right depth-first traversal order, wrapping back to
    /// the last leaf when called on the first one. Returns null if
    /// <paramref name="current"/> is not in the tree or the tree
    /// contains a single leaf. Mirror of <see cref="NextLeafInOrder"/>.
    /// </summary>
    public static LeafPane? PreviousLeafInOrder(PaneNode root, LeafPane current)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(current);

        var ordered = Leaves(root).ToList();
        if (ordered.Count <= 1) return null;

        var idx = ordered.IndexOf(current);
        if (idx < 0) return null;
        return ordered[(idx - 1 + ordered.Count) % ordered.Count];
    }

    /// <summary>
    /// Reset every <see cref="SplitPane.Ratio"/> in the tree to 0.5,
    /// giving all leaves equal space. No-op for parity with Zig/Swift
    /// on a single leaf.
    /// </summary>
    public static void Equalize(PaneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is not SplitPane split) return;
        split.Ratio = 0.5;
        Equalize(split.Child1);
        Equalize(split.Child2);
    }

    /// <summary>
    /// Move the divider of the nearest matching-orientation ancestor
    /// of <paramref name="current"/> by <paramref name="delta"/>.
    /// Up/Down look for a horizontal (top/bottom) split; Left/Right
    /// look for a vertical (left/right) split. Left/Up shrink Child1,
    /// Right/Down grow it. Ratio is clamped to
    /// [<see cref="SplitPane.MinRatio"/>, <see cref="SplitPane.MaxRatio"/>]
    /// so no pane collapses below a usable size; same clamp the
    /// mouse-drag Splitter uses.
    ///
    /// Returns true if a target divider was found and adjusted, false
    /// when no matching ancestor exists (e.g. resize Up but the
    /// active leaf has no horizontal-split ancestor) or
    /// <paramref name="current"/> is the root leaf.
    /// </summary>
    public static bool ResizeSplit(
        PaneNode root,
        LeafPane current,
        ResizeDirection direction,
        double delta)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(current);

        var targetOrientation = direction switch
        {
            ResizeDirection.Up or ResizeDirection.Down => PaneOrientation.Horizontal,
            ResizeDirection.Left or ResizeDirection.Right => PaneOrientation.Vertical,
            _ => PaneOrientation.Vertical,
        };

        // Walk up from `current` looking for the nearest ancestor
        // split whose orientation matches the resize direction.
        // FindParent is itself O(n), so the loop is O(depth * n);
        // trees are tiny in practice (depth <= 4 for typical layouts)
        // so this is fine and beats the bookkeeping cost of parent
        // back-pointers, consistent with the file header rationale.
        PaneNode node = current;
        SplitPane? target = null;
        while (true)
        {
            var parent = FindParent(root, node);
            if (parent is null) break;
            var (split, _) = parent.Value;
            if (split.Orientation == targetOrientation)
            {
                target = split;
                break;
            }
            node = split;
        }

        if (target is null) return false;

        var signedDelta = direction switch
        {
            ResizeDirection.Left or ResizeDirection.Up => -delta,
            ResizeDirection.Right or ResizeDirection.Down => delta,
            _ => 0.0,
        };
        target.Ratio = Math.Clamp(
            target.Ratio + signedDelta,
            SplitPane.MinRatio,
            SplitPane.MaxRatio);
        return true;
    }
}
