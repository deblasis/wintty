using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests;

/// <summary>
/// Undoing a pane close must bring the pane back, in every layout.
///
/// <see cref="PaneTree.Close"/> rewires the live tree in place whenever the
/// closed leaf's parent has a grandparent (any layout of three or more
/// panes), so a snapshot is only a faithful pre-close state if it is taken
/// BEFORE <c>Close</c> runs. These tests pin both halves of that: the
/// mutation contract of <c>Close</c> itself, and what the history keeps for
/// each order of capture and close. The call-site ordering in the shell is
/// pinned separately by <c>PaneCloseUndoSnapshotWiringTests</c>.
/// </summary>
public sealed class PaneCloseUndoSnapshotTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SplitPane Vs(PaneNode a, PaneNode b) => new(PaneOrientation.Vertical, a, b);
    private static SplitPane Hs(PaneNode a, PaneNode b) => new(PaneOrientation.Horizontal, a, b);

    private sealed record Layout(string Name, Func<(PaneNode Root, LeafPane[] Leaves)> Build);

    private static Layout L(string name, int count, Func<LeafPane[], PaneNode> shape)
        => new(name, () =>
        {
            var leaves = Enumerable.Range(0, count).Select(_ => new LeafPane()).ToArray();
            return (shape(leaves), leaves);
        });

    private static readonly Layout[] Layouts =
    {
        // 3 leaves. Parent-is-root cases and nested cases, child1 and child2.
        L("3: V(a, H(b, c))", 3, x => Vs(x[0], Hs(x[1], x[2]))),
        L("3: V(H(a, b), c)", 3, x => Vs(Hs(x[0], x[1]), x[2])),
        // 4 leaves.
        L("4: V(H(a, b), H(c, d))", 4, x => Vs(Hs(x[0], x[1]), Hs(x[2], x[3]))),
        L("4: V(a, V(b, H(c, d)))", 4, x => Vs(x[0], Vs(x[1], Hs(x[2], x[3])))),
        // 5 leaves.
        L("5: V(H(a, b), V(c, H(d, e)))", 5, x => Vs(Hs(x[0], x[1]), Vs(x[2], Hs(x[3], x[4])))),
        L("5: H(V(a, V(b, c)), V(d, e))", 5, x => Hs(Vs(x[0], Vs(x[1], x[2])), Vs(x[3], x[4]))),
    };

    /// <summary>Every (layout, target index) pair: every leaf of every
    /// layout is closed once, so child1 and child2 positions, nested
    /// leaves and leaves whose parent is the root are all covered.</summary>
    public static IEnumerable<object[]> EveryLeafOfEveryLayout()
    {
        foreach (var layout in Layouts)
        {
            var count = layout.Build().Leaves.Length;
            for (var i = 0; i < count; i++)
                yield return new object[] { Array.IndexOf(Layouts, layout), i };
        }
    }

    private static PaneSnapshot SnapshotOf(PaneNode root, LeafPane active)
        => new(PaneTree.Clone(root), active, null, PaneOpKind.Close);

    // -- The mutation contract of Close ------------------------------------

    [Fact]
    public void Close_ParentHasGrandparent_RewiresTheLiveTreeInPlace()
    {
        // The contract the call site must respect: with a grandparent,
        // Close returns the SAME root object, and that root no longer
        // contains the target. The tree the caller holds is already the
        // post-close tree when Close returns.
        var a = new LeafPane();
        var target = new LeafPane();
        var c = new LeafPane();
        var root = Vs(a, Hs(target, c));

        var returned = PaneTree.Close(root, target);

        Assert.Same(root, returned);
        Assert.DoesNotContain(target, PaneTree.Leaves(root));
        Assert.Equal(new[] { a, c }, PaneTree.Leaves(root).ToArray());
    }

    [Fact]
    public void Close_ParentIsRoot_LeavesTheOldRootUntouched()
    {
        // The other branch: with exactly two panes Close hands back the
        // sibling and does not touch the old root, which is why a snapshot
        // taken after Close happened to be correct for two panes.
        var target = new LeafPane();
        var sibling = new LeafPane();
        var root = Vs(target, sibling);

        var returned = PaneTree.Close(root, target);

        Assert.Same(sibling, returned);
        Assert.Contains(target, PaneTree.Leaves(root));
        Assert.Same(target, root.Child1);
        Assert.Same(sibling, root.Child2);
    }

    // -- A snapshot taken before Close keeps the pane ----------------------

    [Theory]
    [MemberData(nameof(EveryLeafOfEveryLayout))]
    public void SnapshotBeforeClose_RetainsTheTarget_AndEveryOtherLeaf(int layoutIndex, int targetIndex)
    {
        var (root, leaves) = Layouts[layoutIndex].Build();
        var target = leaves[targetIndex];

        var snapshot = SnapshotOf(root, target);
        var live = PaneTree.Close(root, target);

        Assert.NotNull(live);
        // The snapshot still has every leaf, the target included, in the
        // original order and with the original identities.
        Assert.Equal(leaves, PaneTree.Leaves(snapshot.Root).ToArray());
        Assert.Contains(target, PaneTree.Leaves(snapshot.Root));
        // And the live tree really did lose it, so the test is not
        // comparing two untouched trees.
        Assert.Equal(leaves.Length - 1, PaneTree.Leaves(live!).Count());
        Assert.DoesNotContain(target, PaneTree.Leaves(live!));
    }

    [Theory]
    [MemberData(nameof(EveryLeafOfEveryLayout))]
    public void CaptureThenClose_ThenUndo_RestoresEveryLeafWithItsIdentity(int layoutIndex, int targetIndex)
    {
        // The host's sequence against the real history: capture, close,
        // then undo hands back a tree with all the original leaves.
        var (root, leaves) = Layouts[layoutIndex].Build();
        var target = leaves[targetIndex];
        var history = new PaneHistory(new FakeTimeProvider(), TimeSpan.FromSeconds(5));

        history.Push(SnapshotOf(root, target));
        var live = PaneTree.Close(root, target)!;
        var restored = history.Undo(SnapshotOf(live, live is LeafPane l ? l : PaneTree.Leaves(live).First()));

        Assert.NotNull(restored);
        Assert.Equal(leaves, PaneTree.Leaves(restored!.Root).ToArray());
    }

    // -- The order that was shipped loses the pane -------------------------

    [Theory]
    [MemberData(nameof(EveryLeafOfEveryLayout))]
    public void SnapshotAfterClose_LosesTheTarget_ExactlyWhenTheParentHasAGrandparent(int layoutIndex, int targetIndex)
    {
        // Documents the defect rather than guarding code: a snapshot taken
        // after Close is the post-close tree whenever the closed leaf's
        // parent is not the root. If this ever stops holding, Close has
        // become non-mutating and the call-site ordering rule can be
        // revisited with the audit in hand.
        var (root, leaves) = Layouts[layoutIndex].Build();
        var target = leaves[targetIndex];
        var parentIsRoot = ReferenceEquals(PaneTree.FindParent(root, target)!.Value.Parent, root);

        PaneTree.Close(root, target);
        var late = SnapshotOf(root, leaves.First(x => x != target));

        if (parentIsRoot)
            Assert.Contains(target, PaneTree.Leaves(late.Root));
        else
            Assert.DoesNotContain(target, PaneTree.Leaves(late.Root));
    }

    // -- Shell lifetime: the history is what keeps the closed pane owned ---

    [Theory]
    [MemberData(nameof(EveryLeafOfEveryLayout))]
    public void ClosedLeaf_IsOrphanedOnEviction_SoItsShellGetsTornDown(int layoutIndex, int targetIndex)
    {
        // The retained shell is released by the host only for leaves the
        // history reports as orphaned. With a correct Close entry the
        // closed leaf is reported when the entry expires.
        var (root, leaves) = Layouts[layoutIndex].Build();
        var target = leaves[targetIndex];
        var time = new FakeTimeProvider();
        var history = new PaneHistory(time, TimeSpan.FromSeconds(5));

        history.Push(SnapshotOf(root, target));
        PaneTree.Close(root, target);

        time.Now += TimeSpan.FromSeconds(6);
        var orphans = history.Prune(time.Now);

        Assert.Contains(target, orphans);
    }

    [Fact]
    public void SnapshotAfterClose_NeverOrphansTheClosedLeaf_SoItsShellWouldLeak()
    {
        // Documents the second-order effect of the shipped order: the Close
        // entry did not reference the closed leaf, so no eviction ever named
        // it, and nothing tore its retained shell down.
        var a = new LeafPane();
        var target = new LeafPane();
        var c = new LeafPane();
        var root = Vs(a, Hs(target, c));
        var time = new FakeTimeProvider();
        var history = new PaneHistory(time, TimeSpan.FromSeconds(5));

        PaneTree.Close(root, target);
        history.Push(SnapshotOf(root, a));

        time.Now += TimeSpan.FromSeconds(6);
        var orphans = history.Prune(time.Now);

        Assert.DoesNotContain(target, orphans);
    }

    [Fact]
    public void ClosedLeaf_AfterUndo_IsNotNamedByLaterEviction()
    {
        // After undo the leaf is back in the live tree and the only entry
        // left is the redo one (the post-close state), which does not hold
        // it. Asserted on the raw Prune result, not a filtered copy, so a
        // history that wrongly named the live leaf as an orphan would fail.
        var a = new LeafPane();
        var target = new LeafPane();
        var c = new LeafPane();
        var root = Vs(a, Hs(target, c));
        var time = new FakeTimeProvider();
        var history = new PaneHistory(time, TimeSpan.FromSeconds(5));

        history.Push(SnapshotOf(root, target));
        var live = PaneTree.Close(root, target)!;
        var restored = history.Undo(SnapshotOf(live, a))!;
        Assert.Contains(target, PaneTree.Leaves(restored.Root));

        time.Now += TimeSpan.FromSeconds(6);
        var orphans = history.Prune(time.Now);

        Assert.DoesNotContain(target, orphans);
    }

    [Fact]
    public void ClosedLeaf_UndoThenRedoThenEviction_IsOrphanedSoItsShellGetsTornDown()
    {
        // close -> undo -> redo: the leaf is out of the live tree again and
        // only the undo entry (the full pre-close state) holds it. When that
        // entry expires the leaf is reported, and the host, which excludes
        // live leaves, would tear its shell down.
        var a = new LeafPane();
        var target = new LeafPane();
        var c = new LeafPane();
        var root = Vs(a, Hs(target, c));
        var time = new FakeTimeProvider();
        var history = new PaneHistory(time, TimeSpan.FromSeconds(5));

        history.Push(SnapshotOf(root, target));
        var afterClose = PaneTree.Close(root, target)!;
        var restored = history.Undo(SnapshotOf(afterClose, a))!;
        var redone = history.Redo(SnapshotOf(restored.Root, target))!;
        var live = PaneTree.Leaves(redone.Root).ToHashSet();
        Assert.DoesNotContain(target, live);

        time.Now += TimeSpan.FromSeconds(6);
        var orphans = history.Prune(time.Now);

        Assert.Contains(target, orphans);
        Assert.Contains(target, orphans.Where(l => !live.Contains(l)));
    }

    [Fact]
    public void Close_ReturnsNull_ExactlyWhenTheTargetIsTheRoot()
    {
        // The shell decides "a pane survives" as !ReferenceEquals(root, leaf)
        // without calling Close; this is the equivalence that makes that
        // sound, over every leaf of every layout and a lone root leaf.
        var lone = new LeafPane();
        Assert.Null(PaneTree.Close(lone, lone));

        foreach (var layout in Layouts)
        {
            var count = layout.Build().Leaves.Length;
            for (var i = 0; i < count; i++)
            {
                var (root, leaves) = layout.Build();
                Assert.False(ReferenceEquals(root, leaves[i]));
                Assert.NotNull(PaneTree.Close(root, leaves[i]));
            }
        }
    }
}
