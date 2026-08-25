using System;
using System.Threading;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class WindowsProcessRunnerTests
{
    [SpawnFact]
    public async System.Threading.Tasks.Task Run_CmdExit42_ReturnsExitCode42()
    {
        var runner = new WindowsProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "exit 42" },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        // Assert the outcome first: a host where the spawn hangs reports
        // ExitCode -1, and "expected Exited, got TimedOut" is the diagnosis,
        // where "expected 42, got -1" only looks like a wrong exit code.
        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(42, result.ExitCode);
    }

    [SpawnFact]
    public async System.Threading.Tasks.Task Run_MissingExe_ReturnsMinusOne()
    {
        var runner = new WindowsProcessRunner();
        var result = await runner.RunAsync("no_such_exe_xyz.exe",
            new string[0], TimeSpan.FromSeconds(2), CancellationToken.None);
        // -1 alone would also be satisfied by a spawn that hung and was
        // killed, which is exactly what this test must not accept.
        Assert.Equal(ProcessOutcome.DidNotStart, result.Outcome);
        Assert.Equal(-1, result.ExitCode);
    }

    [SpawnFact]
    public async System.Threading.Tasks.Task Run_LongPing_TimeoutKills()
    {
        // Plan originally used `cmd.exe /c pause`, but WindowsProcessRunner
        // doesn't redirect stdin (UseShellExecute=false + CreateNoWindow=true
        // leaves pause with no console to read from, so it returns exit 0
        // immediately). Ping with -n 60 blocks on the timer regardless of
        // stdin, so it reliably exercises the 500ms timeout-kill path.
        var runner = new WindowsProcessRunner();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync("ping.exe",
            new[] { "-n", "60", "127.0.0.1" },
            TimeSpan.FromMilliseconds(500), CancellationToken.None);
        sw.Stop();
        // The elapsed range alone is also satisfied by a spawn that never
        // started and stalled; only the outcome proves the kill path ran.
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.Equal(-1, result.ExitCode);
        Assert.InRange(sw.ElapsedMilliseconds, 400, 3000);
    }
}
