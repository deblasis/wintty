using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Ghostty.Core.Profiles.Tracking;
using Xunit;

namespace Ghostty.Tests.Ssh;

/// <summary>
/// Runs the probe against a real <c>ssh.exe</c>. The ProxyCommand keeps
/// ssh alive without opening a connection anywhere, and makes ssh spawn
/// a child, so the snapshot has the same shape a jump host produces.
/// Skipped where there is no Windows OpenSSH client.
/// </summary>
public class SshPaneProbeLiveTests
{
    private static string? SshExe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void FindsTheTargetOfARunningSsh()
    {
        if (SshExe() is not { } ssh || !OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000)) return;

        var psi = new ProcessStartInfo
        {
            FileName = ssh,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[]
        {
            "-p", "2222", "-l", "bob", "-i", @"C:\keys\my key",
            "-o", "ProxyCommand=ping -n 30 127.0.0.1",
            "-o", "BatchMode=yes",
            "probe-target.invalid",
        })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        try
        {
            // Give ssh a moment to start its proxy child.
            Thread.Sleep(500);
            Assert.False(process.HasExited, "ssh exited before it could be inspected");

            var target = SshPaneProbe.Find(process.Id);

            Assert.NotNull(target);
            Assert.Equal("probe-target.invalid", target!.Destination);
            Assert.Equal("bob", target.User);
            Assert.Equal(2222, target.Port);
            Assert.Equal(
                ["-i", @"C:\keys\my key", "-o", "ProxyCommand=ping -n 30 127.0.0.1"],
                target.PassThrough);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void AProcessThatIsNotSshIsNotATarget()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000)) return;
        Assert.Null(SshPaneProbe.Find(Environment.ProcessId));
        Assert.Null(SshPaneProbe.Find(0));
    }
}
