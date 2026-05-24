using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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

    [Fact]
    public async Task Track_WslWithDistribution_ReportsAutoForWslDistro()
    {
        // Probe wsl: CI runners often lack it. Silent early-out keeps CI green
        // while still exercising the full path on a dev machine with wsl set up.
        var (ok, distro) = ProbeWsl();
        if (!ok || string.IsNullOrEmpty(distro))
        {
            _output.WriteLine("wsl not available - skipping");
            return;
        }
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
            if (string.Equals(e.ExeBasename, "wsl.exe", StringComparison.OrdinalIgnoreCase)
                && !tcs.Task.IsCompleted)
            {
                tcs.SetResult((e.ExeBasename!, e.CommandLine));
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

        try { pwsh.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>
    /// Probes whether wsl is installed and at least one distro exists.
    /// <c>wsl --list --quiet</c> emits UTF-16 LE with a BOM on Windows, so the
    /// raw stdout has interleaved null bytes when read as the default encoding.
    /// We strip those before splitting; the result is the first non-empty line.
    /// </summary>
    private static (bool ok, string? distro) ProbeWsl()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "--list --quiet",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return (false, null);
            if (!p.WaitForExit(2000)) return (false, null);
            if (p.ExitCode != 0) return (false, null);

            var raw = p.StandardOutput.ReadToEnd();
            var cleaned = new string(raw.Where(c => c != '\0' && c != '﻿').ToArray());
            var first = cleaned
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            return string.IsNullOrEmpty(first) ? (false, null) : (true, first);
        }
        catch
        {
            return (false, null);
        }
    }
}
