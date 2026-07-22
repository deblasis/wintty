using System;
using Ghostty.Core.Notifications;
using Xunit;

namespace Ghostty.Tests.Notifications;

public class NotificationServiceTests
{
    private static Notice Info(string title, string? dedup = null, Action? onDismiss = null) =>
        new() { Title = title, DedupKey = dedup, OnDismiss = onDismiss };

    [Fact]
    public void Show_adds_to_active()
    {
        var s = new NotificationService();
        s.Show(Info("a"));
        Assert.Single(s.Active);
        Assert.Equal("a", s.Active[0].Title);
    }

    [Fact]
    public void Show_dedups_by_key_while_active()
    {
        var s = new NotificationService();
        s.Show(Info("first", dedup: "k"));
        s.Show(Info("second", dedup: "k"));
        Assert.Single(s.Active);
        Assert.Equal("first", s.Active[0].Title);
    }

    [Fact]
    public void Show_without_dedup_key_always_adds()
    {
        var s = new NotificationService();
        s.Show(Info("a"));
        s.Show(Info("a"));
        Assert.Equal(2, s.Active.Count);
    }

    [Fact]
    public void Dismiss_removes_and_fires_ondismiss_once()
    {
        var s = new NotificationService();
        var calls = 0;
        var n = Info("a", onDismiss: () => calls++);
        s.Show(n);

        s.Dismiss(n);
        Assert.Empty(s.Active);
        Assert.Equal(1, calls);

        s.Dismiss(n); // not active anymore
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dismiss_of_unknown_notice_is_noop()
    {
        var s = new NotificationService();
        s.Show(Info("a"));
        s.Dismiss(Info("other"));
        Assert.Single(s.Active);
    }

    [Fact]
    public void Dismiss_frees_the_dedup_key_for_reuse()
    {
        var s = new NotificationService();
        var first = Info("first", dedup: "k");
        s.Show(first);
        s.Dismiss(first);
        s.Show(Info("second", dedup: "k"));
        Assert.Single(s.Active);
        Assert.Equal("second", s.Active[0].Title);
    }

    [Fact]
    public void Show_null_throws()
    {
        var s = new NotificationService();
        Assert.Throws<ArgumentNullException>(() => s.Show(null!));
    }
}
