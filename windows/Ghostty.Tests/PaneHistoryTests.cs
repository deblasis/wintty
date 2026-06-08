using System;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests;

public sealed class PaneHistoryTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PaneSnapshot Snap(PaneOpKind kind, LeafPane? active = null)
        => new(new LeafPane(), active ?? new LeafPane(), null, kind);

    private static PaneHistory New(FakeTimeProvider? time = null, double timeoutSeconds = 5)
        => new(time ?? new FakeTimeProvider(), TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public void Push_ThenUndo_ReturnsPushedSnapshot()
    {
        var h = New();
        var pre = Snap(PaneOpKind.Split);
        h.Push(pre);

        Assert.True(h.CanUndo);
        var current = Snap(PaneOpKind.Split);
        var restored = h.Undo(current);
        Assert.Same(pre, restored);
        Assert.False(h.CanUndo);
        Assert.True(h.CanRedo);
    }

    [Fact]
    public void Undo_Empty_ReturnsNull()
    {
        var h = New();
        Assert.Null(h.Undo(Snap(PaneOpKind.Split)));
    }

    [Fact]
    public void Redo_AfterUndo_ReturnsTheStateThatWasCurrentAtUndo()
    {
        var h = New();
        h.Push(Snap(PaneOpKind.Split));
        var atUndo = Snap(PaneOpKind.Split);
        h.Undo(atUndo);

        var atRedo = Snap(PaneOpKind.Split);
        var restored = h.Redo(atRedo);
        Assert.Same(atUndo, restored); // redo restores what was live when we undid
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var h = New();
        h.Push(Snap(PaneOpKind.Split));
        h.Undo(Snap(PaneOpKind.Split));
        Assert.True(h.CanRedo);

        h.Push(Snap(PaneOpKind.Close));
        Assert.False(h.CanRedo); // a new op invalidates redo
    }

    [Fact]
    public void Push_CoalescesConsecutiveResizes()
    {
        var h = New();
        h.Push(Snap(PaneOpKind.Resize)); // captures pre-burst ratios
        h.Push(Snap(PaneOpKind.Resize)); // coalesced away
        h.Push(Snap(PaneOpKind.Resize)); // coalesced away

        h.Undo(Snap(PaneOpKind.Resize));
        Assert.False(h.CanUndo); // single undo step back to pre-burst
    }

    [Fact]
    public void Push_ResizeAfterNonResize_NotCoalesced()
    {
        var h = New();
        h.Push(Snap(PaneOpKind.Split));
        h.Push(Snap(PaneOpKind.Resize));
        h.Push(Snap(PaneOpKind.Resize)); // coalesced into the prior resize

        h.Undo(Snap(PaneOpKind.Resize));
        Assert.True(h.CanUndo); // resize entry consumed, split remains
    }

    [Fact]
    public void Prune_RemovesEntriesOlderThanTimeout()
    {
        var time = new FakeTimeProvider();
        var h = New(time, timeoutSeconds: 5);
        h.Push(Snap(PaneOpKind.Split));

        time.Now = time.Now.AddSeconds(6); // past the 5s window
        h.Prune(time.Now);

        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Prune_KeepsFreshEntries()
    {
        var time = new FakeTimeProvider();
        var h = New(time, timeoutSeconds: 5);
        h.Push(Snap(PaneOpKind.Split));

        time.Now = time.Now.AddSeconds(2);
        h.Prune(time.Now);

        Assert.True(h.CanUndo);
    }

    [Fact]
    public void Prune_ReturnsLeavesUniqueToEvictedEntries()
    {
        var time = new FakeTimeProvider();
        var h = New(time, timeoutSeconds: 5);

        // A close snapshot whose tree still references the closed leaf.
        var closed = new LeafPane();
        var snap = new PaneSnapshot(closed, closed, null, PaneOpKind.Close);
        h.Push(snap);

        time.Now = time.Now.AddSeconds(6);
        var orphans = h.Prune(time.Now);

        Assert.Contains(closed, orphans);
    }

    [Fact]
    public void Prune_DoesNotReturnLeavesStillReferencedByOtherEntries()
    {
        var time = new FakeTimeProvider();
        var h = New(time, timeoutSeconds: 5);

        var shared = new LeafPane();
        h.Push(new PaneSnapshot(shared, shared, null, PaneOpKind.Close)); // entry A (old)
        time.Now = time.Now.AddSeconds(3);
        h.Push(new PaneSnapshot(shared, shared, null, PaneOpKind.Equalize)); // entry B (newer)

        time.Now = time.Now.AddSeconds(3); // A is 6s old, B is 3s old
        var orphans = h.Prune(time.Now);

        Assert.DoesNotContain(shared, orphans); // still referenced by B
    }

    [Fact]
    public void Clear_ReturnsAllReferencedLeaves()
    {
        var h = New();
        var a = new LeafPane();
        var b = new LeafPane();
        h.Push(new PaneSnapshot(a, a, null, PaneOpKind.Close));
        h.Push(new PaneSnapshot(b, b, null, PaneOpKind.Close));

        var all = h.Clear();

        Assert.Contains(a, all);
        Assert.Contains(b, all);
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }
}
