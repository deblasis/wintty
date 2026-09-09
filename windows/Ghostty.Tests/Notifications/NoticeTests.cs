using System;
using Ghostty.Core.Notifications;
using Xunit;

namespace Ghostty.Tests.Notifications;

public class NoticeTests
{
    [Fact]
    public void Defaults_AreSticky_AndUnfocused()
    {
        var n = new Notice { Title = "t" };
        Assert.Null(n.AutoDismissAfter);
        Assert.False(n.FocusOnShow);
    }

    [Fact]
    public void Transient_CarriesItsTimeout()
    {
        var n = new Notice { Title = "t", AutoDismissAfter = TimeSpan.FromSeconds(4), FocusOnShow = true };
        Assert.Equal(TimeSpan.FromSeconds(4), n.AutoDismissAfter);
        Assert.True(n.FocusOnShow);
    }
}
