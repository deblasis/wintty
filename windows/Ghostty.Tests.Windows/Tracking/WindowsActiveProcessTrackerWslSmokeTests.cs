using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Ghostty.Tests.Windows.Tracking;

[Collection(TrackerSmoke.SerialCollection)]
public sealed class WindowsActiveProcessTrackerWslSmokeTests
{
    private readonly ITestOutputHelper _output;

    public WindowsActiveProcessTrackerWslSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // WslFact rather than an early-out inside the body: CI runners often lack
    // wsl, and a test that returns early is recorded as a pass, which is the
    // same green as a test that proved something. The gate also spawns pwsh
    // first, so "wsl is not installed" stays distinct from "nothing spawns on
    // this host" -- the two the old check could not tell apart.
    [WslFact]
    public async Task Track_WslWithDistribution_ReportsAutoForWslDistro()
    {
        var distro = WslDistro.Name!;
        _output.WriteLine($"probed distro: {distro}");

        // Spawn pwsh -> wsl.exe --distribution <distro> -- sleep 60
        // pwsh is the root we register; wsl.exe is the descendant the walker
        // should observe.
        //
        // sleep 60, not sleep 5: the old five seconds had to outlast pwsh's
        // cold start AND however long the tracker took to look, so on a
        // loaded machine wsl.exe could exit before it was ever observed --
        // the test then failed for the scenario ending early rather than for
        // anything the tracker did. The process is killed in the finally
        // below, so a longer sleep costs nothing on a healthy run.
        using var pwsh = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            Arguments = $"-NoLogo -NoProfile -Command \"& wsl.exe --distribution {distro} -- sleep 60\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(pwsh);

        try
        {
            using var tracker = TrackerSmoke.NewTracker();
            var tcs = new TaskCompletionSource<(string Exe, string? Cmd)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<string>();
            var sw = Stopwatch.StartNew();

            tracker.Changed += (_, e) =>
            {
                lock (observed)
                {
                    observed.Add($"{sw.ElapsedMilliseconds}ms pid={e.RootPid} exe={e.ExeBasename ?? "<null>"} cmd={e.CommandLine ?? "<null>"}");
                }
                // We want wsl.exe specifically. The broker filter already removes
                // wslhost / conhost / OpenConsole. Anything deeper (the linux side)
                // is invisible to the Win32 walker.
                if (string.Equals(e.ExeBasename, "wsl.exe", StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetResult((e.ExeBasename!, e.CommandLine));
                }
            };
            tracker.Register(pwsh!.Id);

            // Precondition, not an assertion about the tracker: wait until the
            // walker can see wsl.exe under pwsh at all. pwsh's cold start and
            // wsl's own launch are not the tracker's doing and used to be
            // charged to its budget.
            var scenario = await TrackerSmoke.AwaitScenario(
                pwsh!.Id,
                e => string.Equals(e, "wsl.exe", StringComparison.OrdinalIgnoreCase));
            Assert.True(
                scenario.Live,
                $"pwsh never produced a wsl.exe descendant within "
                + $"{TrackerSmoke.ScenarioBudgetMs} ms, so there was nothing for the "
                + $"tracker to report: this is the host, pwsh or wsl, not the tracker.");
            _output.WriteLine(
                $"scenario live: {scenario.Exe} after {scenario.ElapsedMs} ms "
                + $"(worst walk {scenario.WalkMs} ms)");

            var deadlineMs = TrackerSmoke.TrackerDeadlineMs(scenario.WalkMs);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(deadlineMs));
            lock (observed)
            {
                foreach (var line in observed)
                    _output.WriteLine(line);
            }
            Assert.True(
                winner == tcs.Task,
                $"the walker could already see wsl.exe under pwsh, but the tracker "
                + $"tracker did not report wsl.exe within {deadlineMs} ms; "
                + $"observed: [{string.Join(", ", observed)}]");

            var (exe, cmd) = await tcs.Task;
            Assert.Equal("wsl.exe", exe, ignoreCase: true);
            Assert.NotNull(cmd);
            Assert.Contains(distro, cmd, StringComparison.OrdinalIgnoreCase);
            _output.WriteLine($"tracker reported {exe} cmd=[{cmd}] after {sw.ElapsedMilliseconds}ms");

            // Full chain: ProcessIconTable.TryMap maps wsl.exe + cmdline to
            // AutoForWslDistro(<distro>). This is the icon UX contract that
            // TabIconViewModel relies on.
            var spec = ProcessIconTable.TryMap(exe, cmd);
            var auto = Assert.IsType<IconSpec.AutoForWslDistro>(spec);
            Assert.Equal(distro, auto.DistroName, ignoreCase: true);
        }
        finally
        {
            try { pwsh!.Kill(entireProcessTree: true); } catch { }
        }
    }
}
