using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Ghostty.Tests.Windows.Tracking;

[Collection(TrackerSmoke.SerialCollection)]
public sealed class WindowsActiveProcessTrackerSmokeTests
{
    private readonly ITestOutputHelper _output;

    public WindowsActiveProcessTrackerSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // PwshFact, not SpawnFact: this spawns pwsh, which is not in-box on
    // Windows and pays a runtime cold start that no cmd.exe measurement
    // predicts.
    [PwshFact]
    public async Task Track_PwshThenCmd_ReportsTransition()
    {
        // Spawn pwsh and have it run a long-lived cmd child that waits on
        // ping. ProcessTreeWalker returns the innermost descendant, so we
        // expect to see "cmd.exe" appear briefly and then "ping.exe" (or
        // "PING.EXE") as the leaf, depending on tick timing relative to
        // cmd's exec of ping. Either is acceptable proof that the
        // walker+tracker end-to-end picked up pwsh's descendants.
        //
        // We DO NOT use "timeout" as the inner command because cmd.exe /c
        // can exec it quickly enough that we skip past cmd in the snapshot
        // window. ping -n 30 is reliably long and reliably a separate exe.
        using var pwsh = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            Arguments = "-NoLogo -NoProfile -Command \"& cmd.exe /c ping -n 30 127.0.0.1 >$null\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(pwsh);

        // finally, not a trailing statement: a failed assertion used to leave
        // pwsh and a 30-second ping alive, so a red run handed the next test
        // -- and the next run of this one -- a machine with extra processes
        // on it.
        try
        {
            // Acceptable: the test scenario spawns pwsh -> cmd -> ping; with the
            // broker filter (conhost / OpenConsole) the walker reports the deepest
            // non-broker descendant. cmd or ping both prove pwsh's descendants are
            // being walked; conhost / OpenConsole appearing means the filter regressed.
            var acceptedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "cmd.exe",
                "ping.exe",
                "PING.EXE",
            };
            var brokerExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "conhost.exe",
                "OpenConsole.exe",
            };

            using var tracker = TrackerSmoke.NewTracker();
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<string>();
            var sw = Stopwatch.StartNew();

            tracker.Changed += (_, e) =>
            {
                lock (observed)
                {
                    observed.Add($"{sw.ElapsedMilliseconds}ms pid={e.RootPid} exe={e.ExeBasename ?? "<null>"}");
                }
                if (e.ExeBasename is not null
                    && brokerExes.Contains(e.ExeBasename)
                    && !tcs.Task.IsCompleted)
                {
                    tcs.SetException(new Xunit.Sdk.XunitException(
                        $"tracker reported broker exe {e.ExeBasename}; filter regressed"));
                }
                else if (e.ExeBasename is not null
                    && acceptedExes.Contains(e.ExeBasename)
                    && !tcs.Task.IsCompleted)
                {
                    tcs.SetResult(e.ExeBasename);
                }
            };
            // Registered before the wait below so the tracker still sees the
            // pwsh -> cmd -> ping transition, not just the settled leaf.
            tracker.Register(pwsh!.Id);

            // Precondition, not an assertion about the tracker: wait until the
            // walker can see a cmd-tree descendant of pwsh at all. Everything
            // before this point is pwsh's cold start and cmd's exec, which the
            // tracker does not control and which used to be charged to its
            // budget.
            var scenario = await TrackerSmoke.AwaitScenario(
                pwsh!.Id, e => acceptedExes.Contains(e));
            Assert.True(
                scenario.Live,
                $"pwsh never produced a cmd-tree descendant within "
                + $"{TrackerSmoke.ScenarioBudgetMs} ms, so there was nothing for the "
                + $"tracker to report: this is the host or pwsh, not the tracker.");
            _output.WriteLine(
                $"scenario live: {scenario.Exe} after {scenario.ElapsedMs} ms "
                + $"(worst walk {scenario.WalkMs} ms)");

            // Only now does the tracker's clock start, and its budget is sized
            // from the walk cost just measured on this host.
            var deadlineMs = TrackerSmoke.TrackerDeadlineMs(scenario.WalkMs);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(deadlineMs));
            lock (observed)
            {
                foreach (var line in observed)
                    _output.WriteLine(line);
            }
            Assert.True(
                winner == tcs.Task,
                $"the walker could already see {scenario.Exe} under pwsh, but the "
                + $"tracker reported no cmd-tree descendant within {deadlineMs} ms; "
                + $"observed: [{string.Join(", ", observed)}]");

            var reported = await tcs.Task;
            Assert.NotNull(reported);
            Assert.Contains(reported!, acceptedExes);
            _output.WriteLine($"tracker reported {reported} after {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            try { pwsh!.Kill(entireProcessTree: true); } catch { }
        }
    }
}
