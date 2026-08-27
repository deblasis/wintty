using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Ghostty.Tests.Windows.Tracking;

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

        // Spawn pwsh -> wsl.exe --distribution <distro> -- sleep 5
        // pwsh is the root we register; wsl.exe is the descendant the walker
        // should observe. The 5s sleep gives the 500 ms tick + 250 ms debounce
        // a comfortable window to fire while wsl.exe is still alive.
        using var pwsh = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            Arguments = $"-NoLogo -NoProfile -Command \"& wsl.exe --distribution {distro} -- sleep 5\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(pwsh);

        try
        {
            using var tracker = new WindowsActiveProcessTracker();
            var tcs = new TaskCompletionSource<(string exe, string? cmd)>(TaskCreationOptions.RunContinuationsAsynchronously);
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

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(8000));
            lock (observed)
            {
                foreach (var line in observed)
                    _output.WriteLine(line);
            }
            Assert.True(
                winner == tcs.Task,
                $"tracker did not report wsl.exe within 8s; observed: [{string.Join(", ", observed)}]");

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
            try { pwsh.Kill(entireProcessTree: true); } catch { }
        }
    }
}
