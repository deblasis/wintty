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

    // Plain Fact: nothing here ever spawns successfully, and there is no
    // elapsed assertion, so spawn latency cannot change the answer. A host
    // that denies process creation outright reaches DidNotStart down the same
    // catch as a missing file. Gating it would delete the only coverage of
    // that path on exactly the hosts the gate fires on.
    [Fact]
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

    [SpawnFact]
    public async System.Threading.Tasks.Task Run_CallerCancelsMidFlight_ReportsCanceled()
    {
        // The timeout is two orders of magnitude past the cancel, so nothing
        // but the caller's token can be what ended this run. Reporting TimedOut
        // here would assert the child was too slow, which it was not.
        var runner = new WindowsProcessRunner();
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync("ping.exe", new[] { "-n", "60", "127.0.0.1" },
            TimeSpan.FromSeconds(60), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        var result = await run;
        Assert.Equal(ProcessOutcome.Canceled, result.Outcome);
        // Cancelled runs stay -1 like every other non-Exited ending, so
        // callers that only read ExitCode see no change.
        Assert.Equal(-1, result.ExitCode);
    }

    [SpawnFact]
    public async System.Threading.Tasks.Task Run_CallerTokenAlreadyCanceled_ReportsCanceled()
    {
        // Same distinction without the race: the token is dead before the
        // runner is called, so the elapsed time cannot be mistaken for a
        // timeout by anything downstream either.
        var runner = new WindowsProcessRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await runner.RunAsync("ping.exe", new[] { "-n", "60", "127.0.0.1" },
            TimeSpan.FromSeconds(60), cts.Token);
        Assert.Equal(ProcessOutcome.Canceled, result.Outcome);
        Assert.Equal(-1, result.ExitCode);
    }
}
