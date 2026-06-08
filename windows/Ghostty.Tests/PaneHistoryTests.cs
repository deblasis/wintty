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
}
