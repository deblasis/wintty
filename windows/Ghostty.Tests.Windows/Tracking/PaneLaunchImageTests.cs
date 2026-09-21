using System;
using System.Diagnostics;
using Ghostty.Core.Profiles.Tracking;
using Xunit;

namespace Ghostty.Tests.Windows.Tracking;

/// <summary>
/// <see cref="PaneLaunchImage"/> against real processes.
///
/// The model's own tests hand it an exe basename and a command line, so they
/// prove what the tab does with an answer and nothing about where the answer
/// comes from. This is the other half: the two syscalls that turn the pid a
/// pane reports into those two strings. A regression here (a buffer handled
/// wrongly, a right the handle no longer carries) leaves every no-profile tab
/// on the generic name with every unit test green.
/// </summary>
public class PaneLaunchImageTests
{
    [Fact]
    public void ItNamesTheExecutableOfARunningProcess()
    {
        using var self = Process.GetCurrentProcess();
        var expected = System.IO.Path.GetFileName(self.MainModule!.FileName);

        var (exe, commandLine) = PaneLaunchImage.TryResolve((uint)Environment.ProcessId);

        // The basename carries its extension, which is how ProcessDisplayName
        // keys its table and how Toolhelp32 spells szExeFile.
        Assert.Equal(expected, exe);
        Assert.EndsWith(".exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(commandLine));
    }

    /// <summary>
    /// A child, not this process: the shape the shell actually resolves,
    /// where the pid names something the test did not start life as. Both
    /// halves are asserted against what was spawned, so a resolver that
    /// answered a constant, or answered about itself, fails.
    ///
    /// There is deliberately no "already exited" case here, and the reason
    /// is about WHEN, not about what still answers. A handle held anywhere
    /// keeps the process OBJECT alive, so whether <c>OpenProcess</c>
    /// succeeds on a dead child depends on who else is holding one, which
    /// this test cannot control. Once it is genuinely terminated both
    /// queries fail even through an open handle -- the right behaviour,
    /// and not one an assertion can schedule. The reachable no-answer case
    /// is <see cref="ItAnswersNothing_ForAPidNothingCouldOwn"/>; the
    /// model's side is <c>TabLaunchNameTests</c>, which drives the null
    /// straight in.
    ///
    /// The child below stays alive because this process holds the write
    /// end of its redirected stdin, so <c>pause</c> never reads EOF. If
    /// that ever stops being true the test goes red rather than passing on
    /// a degraded answer, which is the direction to fail in.
    /// </summary>
    [Fact]
    public void ItNamesTheExecutableOfAChildItSpawned()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
        })!;
        try
        {
            var (exe, commandLine) = PaneLaunchImage.TryResolve((uint)child.Id);

            Assert.Equal("cmd.exe", exe);
            Assert.Contains("cmd", commandLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/c pause", commandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
    }

    [Fact]
    public void ItAnswersNothing_ForAPidNothingCouldOwn()
    {
        // The idle process is pid 0 and no handle can be opened for it.
        var (exe, commandLine) = PaneLaunchImage.TryResolve(0);

        Assert.Null(exe);
        Assert.Null(commandLine);
    }

    // The last-segment rule itself is TabLabel.FolderName, shared with the
    // tab label rather than copied here; TabLabelCwdTests covers it,
    // including the image-path shapes this caller feeds it.
}
