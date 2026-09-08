using System;
using System.Linq;
using Ghostty.Core.Notifications;
using Ghostty.Core.Taskbar;
using Xunit;

namespace Ghostty.Tests.Taskbar;

public class TaskbarBadgeArbiterTests
{
    private static TaskbarBadge Bell(string signature) => new(
        "bell", TaskbarBadgeKind.Bell, signature,
        "Bell", "A bell rang in a background tab.", NoticeSeverity.Informational,
        Array.Empty<NoticeAction>());

    private static TaskbarBadge UpdateError(string message) => new(
        "update", TaskbarBadgeKind.UpdateError, "Error|Renew|" + message,
        "Update error", message, NoticeSeverity.Warning,
        Array.Empty<NoticeAction>());

    private static (TaskbarBadgeArbiter arbiter, FakeTaskbarBadgeSink sink, NotificationService notices) New()
    {
        var sink = new FakeTaskbarBadgeSink();
        var notices = new NotificationService();
        return (new TaskbarBadgeArbiter(sink, notices), sink, notices);
    }

    [Fact]
    public void Raise_ShowsOverlayAndNoticeTogether()
    {
        var (a, sink, notices) = New();
        a.Raise(Bell("1"));
        Assert.Equal(TaskbarBadgeKind.Bell, sink.Current);
        var notice = Assert.Single(notices.Active);
        Assert.Equal("badge:bell", notice.DedupKey);
        Assert.Equal("Bell", notice.Title);
        Assert.True(notice.IsClosable);
    }

    [Fact]
    public void DismissingTheNotice_ClearsTheOverlay_AndSuppressesTheSameSignature()
    {
        var (a, sink, notices) = New();
        a.Raise(UpdateError("not entitled"));
        notices.Dismiss(notices.Active.Single());
        Assert.Null(sink.Current);
        Assert.Empty(a.ActiveKeys);

        a.Raise(UpdateError("not entitled"));
        Assert.Null(sink.Current);
        Assert.Empty(notices.Active);

        a.Raise(UpdateError("server error"));
        Assert.Equal(TaskbarBadgeKind.UpdateError, sink.Current);
        Assert.Single(notices.Active);
    }

    [Fact]
    public void RepeatedRaise_WithSameSignature_WritesOnce()
    {
        var (a, sink, notices) = New();
        a.Raise(Bell("1"));
        a.Raise(Bell("1"));
        Assert.Single(sink.Writes);
        Assert.Single(notices.Active);
    }

    [Fact]
    public void NewSignature_ReplacesTheNotice()
    {
        var (a, _, notices) = New();
        a.Raise(UpdateError("a"));
        a.Raise(UpdateError("b"));
        var notice = Assert.Single(notices.Active);
        Assert.Equal("b", notice.Message);
    }

    [Fact]
    public void HighestPriorityKind_OwnsTheOverlay()
    {
        var (a, sink, _) = New();
        a.Raise(UpdateError("x"));
        a.Raise(Bell("1"));
        Assert.Equal(TaskbarBadgeKind.Bell, sink.Current);
        a.Clear("bell");
        Assert.Equal(TaskbarBadgeKind.UpdateError, sink.Current);
        a.Clear("update");
        Assert.Null(sink.Current);
    }

    [Fact]
    public void Clear_LeavesTheNoticeForReview()
    {
        var (a, sink, notices) = New();
        a.Raise(Bell("1"));
        a.Clear("bell");
        Assert.Null(sink.Current);
        Assert.Single(notices.Active);
        notices.Dismiss(notices.Active.Single());
        Assert.Empty(notices.Active);
        Assert.Null(sink.Current);
    }

    [Fact]
    public void Bell_WithAFreshSignature_RaisesAgainAfterDismiss()
    {
        var (a, sink, notices) = New();
        a.Raise(Bell("1"));
        notices.Dismiss(notices.Active.Single());
        a.Raise(Bell("2"));
        Assert.Equal(TaskbarBadgeKind.Bell, sink.Current);
    }

    [Fact]
    public void DismissedSignature_StaysSuppressedWhileStillOngoing_ButRecursAfterClear()
    {
        var (a, sink, notices) = New();
        a.Raise(UpdateError("not entitled"));
        notices.Dismiss(notices.Active.Single());

        // The producer keeps polling and the same failure is still
        // happening: an acknowledged, merely-ongoing situation must not
        // re-raise.
        a.Raise(UpdateError("not entitled"));
        Assert.Null(sink.Current);
        Assert.Empty(notices.Active);

        // The producer reports the situation resolved.
        a.Clear("update");
        Assert.Null(sink.Current);

        // It genuinely recurs later with the identical signature. This is a
        // new episode of the same problem, not the one already reviewed and
        // dismissed, so it must badge again.
        a.Raise(UpdateError("not entitled"));
        Assert.Equal(TaskbarBadgeKind.UpdateError, sink.Current);
        Assert.Single(notices.Active);
    }

    [Fact]
    public void SupersedingTheOverlayOwner_DoesNotFlicker()
    {
        var (a, sink, _) = New();
        a.Raise(UpdateError("a"));
        var writesBeforeSupersede = sink.Writes.Count;

        a.Raise(UpdateError("b"));

        // The kind that owns the slot does not change across a signature
        // change on the same key, so nothing about the visible overlay
        // needs to change. The old double-Recompute bug briefly cleared
        // the overlay (a (null, "") write) before re-showing it; that
        // transient write must not appear.
        Assert.DoesNotContain(sink.Writes.Skip(writesBeforeSupersede), w => w.Kind is null);
    }

    [Fact]
    public void ActiveKeys_IsASnapshot_NotALiveView()
    {
        var (a, _, _) = New();
        a.Raise(Bell("1"));
        var keys = a.ActiveKeys;

        a.Raise(UpdateError("x"));

        Assert.Single(keys);
        Assert.Equal(2, a.ActiveKeys.Count);
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        var sink = new FakeTaskbarBadgeSink();
        var notices = new NotificationService();
        Assert.Throws<ArgumentNullException>(() => new TaskbarBadgeArbiter(null!, notices));
        Assert.Throws<ArgumentNullException>(() => new TaskbarBadgeArbiter(sink, null!));
    }

    [Fact]
    public void Raise_RejectsNullBadge()
    {
        var (a, _, _) = New();
        Assert.Throws<ArgumentNullException>(() => a.Raise(null!));
    }
}
