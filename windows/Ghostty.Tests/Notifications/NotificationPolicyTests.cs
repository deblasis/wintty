using Ghostty.Core.Notifications;
using Xunit;

namespace Ghostty.Tests.Notifications;

public class NotificationPolicyTests
{
    // --- DesktopNotification ---

    [Fact]
    public void DesktopNotification_Unfocused_Returns_Request_With_Content()
    {
        var req = NotificationPolicy.DesktopNotification("Build done", "Succeeded", "0x1234", isSurfaceFocused: false);
        Assert.NotNull(req);
        Assert.Equal("Build done", req!.Title);
        Assert.Equal("Succeeded", req.Body);
        Assert.Equal("0x1234", req.SurfaceKey);
    }

    [Fact]
    public void DesktopNotification_Focused_Is_Suppressed()
    {
        var req = NotificationPolicy.DesktopNotification("Build done", "Succeeded", "0x1234", isSurfaceFocused: true);
        Assert.Null(req);
    }

    // --- ChildExited ---

    [Fact]
    public void ChildExited_Unfocused_NonZeroRuntime_ZeroCode_Returns_Normal_Exit()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 0, runtimeMs: 5000, surfaceKey: "0x99", isSurfaceFocused: false);
        Assert.NotNull(req);
        Assert.Equal("0x99", req!.SurfaceKey);
        Assert.Contains("0", req.Body); // body mentions code 0 / normal exit; see policy
    }

    [Fact]
    public void ChildExited_Unfocused_NonZeroCode_Mentions_The_Code()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 137, runtimeMs: 5000, surfaceKey: "0x99", isSurfaceFocused: false);
        Assert.NotNull(req);
        Assert.Contains("137", req!.Body);
    }

    [Fact]
    public void ChildExited_Focused_Is_Suppressed()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 1, runtimeMs: 5000, surfaceKey: "0x99", isSurfaceFocused: true);
        Assert.Null(req);
    }

    [Fact]
    public void ChildExited_ZeroRuntime_Is_Suppressed_Even_When_Unfocused()
    {
        // runtime_ms == 0 rules out exit codes reported at launch
        // (matches macOS's timetime_ms > 0 guard).
        var req = NotificationPolicy.ChildExited(exitCode: 1, runtimeMs: 0, surfaceKey: "0x99", isSurfaceFocused: false);
        Assert.Null(req);
    }
}
