using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A transient notice must actually leave on its own, and Enter or Space
/// must dismiss a focused one; both live in AddBar, the only place a bar is
/// born.
/// </summary>
public sealed class NotificationHostWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Controls.Notifications.NotificationHost.xaml.cs");

    [Fact]
    public void AddBar_ArmsTheAutoDismissTimer_WhenTheNoticeAsksForOne()
    {
        var body = Host().Method("AddBar").Body!.ToString();
        Assert.Contains("AutoDismissAfter", body);
        Assert.Contains("CreateTimer", body);
    }

    [Fact]
    public void AddBar_HandlesEnterAndSpace()
    {
        var body = Host().Method("AddBar").Body!.ToString();
        Assert.Contains("VirtualKey.Enter", body);
        Assert.Contains("VirtualKey.Space", body);
        Assert.Contains("FocusOnShow", body);
    }

    [Fact]
    public void RemoveBar_StopsTheTimer_AndReturnsFocus()
    {
        var body = Host().Method("RemoveBar").Body!.ToString();
        Assert.Contains("Stop()", body);
        Assert.Contains("FocusReturn", body);
    }
}
