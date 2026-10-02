using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Windows.Themes;

/// <summary>
/// <see cref="ThemePreviewTarget"/>'s readers against real processes and
/// real files. The unit tests inject identities as strings, so they cannot
/// see the one thing that matters here: that the kernel's image path of a
/// process and the file a launch named resolve to the same value. A
/// process started through a junction reports its launch spelling to
/// itself and the resolved target to everyone else, so comparing those two
/// spellings directly misses the same install.
/// </summary>
public class ThemePreviewTargetProcessTests
{
    [Fact]
    public void OwnIdentityMatchesTheFileThisProcessWasStartedFrom()
    {
        var own = ThemePreviewTarget.TryReadIdentity((uint)Environment.ProcessId);

        Assert.NotNull(own);
        Assert.True(ThemePreviewTarget.SameIdentity(
            own, ThemePreviewTarget.TryReadFileIdentity(Environment.ProcessPath!)));
        Assert.True(ThemePreviewTarget.RunsOwnExecutable(Environment.ProcessId));
    }

    [Fact]
    public void AChildStartedThroughAJunctionHasItsTargetsIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "wintty-preview-target-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(real);
        Process? child = null;
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            File.Copy(Path.Combine(system, "PING.EXE"), Path.Combine(real, "PING.EXE"));
            using (var mklink = Process.Start(new ProcessStartInfo(
                Path.Combine(system, "cmd.exe"), $"/c mklink /J \"{junction}\" \"{real}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!)
            {
                mklink.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }

            var launched = Path.Combine(junction, "PING.EXE");
            child = Process.Start(new ProcessStartInfo(launched, "-n 60 127.0.0.1")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;

            var identity = ThemePreviewTarget.TryReadIdentity((uint)child.Id);

            Assert.NotNull(identity);
            Assert.True(ThemePreviewTarget.SameIdentity(
                identity, ThemePreviewTarget.TryReadFileIdentity(Path.Combine(real, "PING.EXE"))));
            Assert.True(ThemePreviewTarget.SameIdentity(
                identity, ThemePreviewTarget.TryReadFileIdentity(launched)));
            // The launch spelling itself is not the identity; that gap is
            // why both sides are resolved rather than compared as spelled.
            Assert.False(ThemePreviewTarget.SameIdentity(identity, launched));
            Assert.False(ThemePreviewTarget.RunsOwnExecutable(child.Id));
        }
        finally
        {
            if (child is not null)
            {
                try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.WaitForExit();
                child.Dispose();
            }
            // A non-recursive delete of a junction removes the link only.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The child's handle stays open through the read, so its pid cannot be
    /// reused by another process; a terminated process answers no image
    /// even through a held handle (see PaneLaunchImageTests).
    /// </summary>
    [Fact]
    public void AnExitedProcessHasNoIdentity()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(system, "cmd.exe"), "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        child.WaitForExit();

        Assert.Null(ThemePreviewTarget.TryReadIdentity((uint)child.Id));
        Assert.False(ThemePreviewTarget.RunsOwnExecutable(child.Id));
    }

    [Fact]
    public void APidNothingCouldOwnHasNoIdentity()
    {
        Assert.Null(ThemePreviewTarget.TryReadIdentity(0));
        Assert.False(ThemePreviewTarget.RunsOwnExecutable(0));
    }

    [Fact]
    public void PipeEndsReportTheProcessOnTheOtherSide()
    {
        var name = "wintty-preview-target-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        var accept = server.WaitForConnectionAsync();
        client.Connect(5000);
        accept.GetAwaiter().GetResult();

        Assert.Equal(Environment.ProcessId, ThemePreviewTarget.ClientPid(server.SafePipeHandle));
        Assert.Equal(Environment.ProcessId, ThemePreviewTarget.ServerPid(client.SafePipeHandle));
    }
}
