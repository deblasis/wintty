using Ghostty.Core.Notifications;
using Xunit;

namespace Ghostty.Tests.Notifications;

public class NotificationPolicyTests
{
    // --- DesktopNotification ---

    [Fact]
    public void DesktopNotification_Inactive_Returns_Request_With_Content()
    {
        var req = NotificationPolicy.DesktopNotification("Build done", "Succeeded", "0x1234", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("Build done", req!.Title);
        Assert.Equal("Succeeded", req.Body);
        Assert.Equal("0x1234", req.SurfaceKey);
    }

    [Fact]
    public void DesktopNotification_Active_Is_Suppressed()
    {
        var req = NotificationPolicy.DesktopNotification("Build done", "Succeeded", "0x1234", isSurfaceActive: true);
        Assert.Null(req);
    }

    // --- ChildExited ---

    [Fact]
    public void ChildExited_Inactive_NonZeroRuntime_ZeroCode_Returns_Normal_Exit()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 0, runtimeMs: 5000, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("0x99", req!.SurfaceKey);
        Assert.Contains("normally", req.Body); // normal-exit copy, not an incidental digit
    }

    [Fact]
    public void ChildExited_Inactive_NonZeroCode_Mentions_The_Code()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 137, runtimeMs: 5000, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Contains("137", req!.Body);
    }

    [Fact]
    public void ChildExited_Active_Is_Suppressed()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 1, runtimeMs: 5000, surfaceKey: "0x99", isSurfaceActive: true);
        Assert.Null(req);
    }

    [Fact]
    public void ChildExited_ZeroRuntime_Is_Suppressed_Even_When_Inactive()
    {
        // runtime_ms == 0 is the launch-failure guard (see NotificationPolicy).
        var req = NotificationPolicy.ChildExited(exitCode: 1, runtimeMs: 0, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.Null(req);
    }
}
