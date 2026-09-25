using System.Linq;
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
        var req = NotificationPolicy.ChildExited(exitCode: 0, runtimeMs: 5000, command: null, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("0x99", req!.SurfaceKey);
        Assert.Contains("normally", req.Body); // normal-exit copy, not an incidental digit
    }

    [Fact]
    public void ChildExited_Inactive_NonZeroCode_Mentions_The_Code()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 137, runtimeMs: 5000, command: null, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Contains("137", req!.Body);
    }

    [Fact]
    public void ChildExited_Active_Is_Suppressed()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 1, runtimeMs: 5000, command: null, surfaceKey: "0x99", isSurfaceActive: true);
        Assert.Null(req);
    }

    [Fact]
    public void ChildExited_ZeroRuntime_Is_Suppressed_Even_When_Inactive()
    {
        // runtime_ms == 0 is the launch-failure guard (see NotificationPolicy).
        var req = NotificationPolicy.ChildExited(exitCode: 1, runtimeMs: 0, command: null, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.Null(req);
    }

    // --- ChildExited names what ran (deblasis/wintty#1193) ---

    // The point of the feature: a person who switched away learns WHICH
    // command died and with what code.
    [Fact]
    public void ChildExited_NamesTheProgramAndTheCode()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 101, runtimeMs: 5000, command: "inventorytool build --release",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("Process exited abnormally", req!.Title);
        Assert.Contains("inventorytool", req.Body);
        Assert.Contains("101", req.Body);
    }

    // The pinned secrets policy: toast text persists in the Action Center
    // history and a command line can carry tokens and passwords, so the
    // toast names the program and NEVER its arguments.
    [Fact]
    public void ChildExited_ArgumentsNeverReachTheToast()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 1, runtimeMs: 5000,
            command: "curl -H \"Authorization: Bearer sk-secret-token-123\" https://api.internal/whoami",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Contains("curl", req.Body);
        Assert.DoesNotContain("sk-secret-token-123", req.Body);
        Assert.DoesNotContain("Authorization", req.Body);
        Assert.DoesNotContain("Bearer", req.Body);
        Assert.DoesNotContain("api.internal", req.Body);
    }

    // An argv reaches the surface behind the in-band direct: marker; the
    // toast names the program inside it, not the marker and not the flags.
    [Fact]
    public void ChildExited_DirectArgvCommandNamesTheProgramInsideIt()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 0, runtimeMs: 5000,
            command: "direct:\"C:\\Program Files\\My Tools\\mytool.exe\" -NoLogo",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("Process exited", req!.Title);
        Assert.Contains("mytool", req.Body);
        Assert.DoesNotContain("direct:", req.Body);
        Assert.DoesNotContain("-NoLogo", req.Body);
        Assert.DoesNotContain("Program Files", req.Body);
    }

    // A long program name is truncated with a visible indicator, never
    // silently shortened and never shown in full.
    [Fact]
    public void ChildExited_LongProgramNameIsTruncatedWithAnIndicator()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 3, runtimeMs: 5000, command: new string('a', 120) + ".exe --flag",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Contains(new string('a', 80) + "\u2026", req.Body);
        Assert.DoesNotContain(new string('a', 81), req.Body);
        Assert.DoesNotContain("--flag", req.Body);
    }

    // Without a command (a plain shell pane) the toast keeps the copy it
    // had before it named anything at all.
    [Fact]
    public void ChildExited_NullCommand_KeepsTheShellCopy()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 7, runtimeMs: 5000, command: null, surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("The shell exited with code 7.", req!.Body);
    }

    [Fact]
    public void ChildExited_EmptyCommand_KeepsTheNormalExitCopy()
    {
        var req = NotificationPolicy.ChildExited(exitCode: 0, runtimeMs: 5000, command: "", surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("The shell exited normally (code 0).", req!.Body);
    }

    // A command whose first token never closes its quote has no readable
    // program; the toast falls back to the copy it had before it named
    // anything.
    [Fact]
    public void ChildExited_UnterminatedQuoteCommand_KeepsTheShellCopy()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 7, runtimeMs: 5000, command: "\"C:\\Program Files\\tool.exe --flag",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("The shell exited with code 7.", req!.Body);
    }

    [Fact]
    public void ChildExited_WhitespaceCommand_KeepsTheShellCopy()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 7, runtimeMs: 5000, command: "   ",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("The shell exited with code 7.", req!.Body);
    }

    // The pinned "program, never arguments" line has one documented
    // exception: a wsl.exe launch names its distro ("WSL: Ubuntu-24.04"),
    // the same words the tab tooltip uses, and the distro is IsPlain-gated.
    // Pinned here so the exception stays a decision, not an accident.
    [Fact]
    public void ChildExited_WslCommandNamesTheDistroLikeTheTabTooltip()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 0, runtimeMs: 5000, command: "wsl.exe -d Ubuntu-24.04",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.Equal("Process exited", req!.Title);
        Assert.Equal("WSL: Ubuntu-24.04 exited normally (code 0).", req.Body);
    }

    // A basename whose 80th UTF-16 unit is the high half of a surrogate
    // pair must lose the whole character, not half of it: a lone surrogate
    // is not a legal XML character and can make the toast sink drop the
    // notification entirely. The visible budget backs off by one instead.
    [Fact]
    public void ChildExited_AstralCharAtTheTruncationBoundaryIsNotSplit()
    {
        var req = NotificationPolicy.ChildExited(
            exitCode: 3, runtimeMs: 5000,
            command: new string('a', 79) + "\U0001F389.exe --flag",
            surfaceKey: "0x99", isSurfaceActive: false);
        Assert.NotNull(req);
        Assert.True(req!.Body.All(c => !char.IsSurrogate(c)), "no lone surrogate reaches the toast body");
        Assert.Contains(new string('a', 79) + "\u2026", req.Body);
        Assert.DoesNotContain("--flag", req.Body);
    }
}
