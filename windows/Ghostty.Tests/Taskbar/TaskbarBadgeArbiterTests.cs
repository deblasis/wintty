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
}
