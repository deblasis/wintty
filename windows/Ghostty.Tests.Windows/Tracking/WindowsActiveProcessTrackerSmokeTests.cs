using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Ghostty.Tests.Windows.Tracking;

public sealed class WindowsActiveProcessTrackerSmokeTests
{
    private readonly ITestOutputHelper _output;

    public WindowsActiveProcessTrackerSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
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

        using var tracker = new WindowsActiveProcessTracker();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<string>();
        var sw = Stopwatch.StartNew();

        // Accept any cmd-tree leaf as proof of the transition: cmd.exe
        // itself (caught between cmd start and exec of ping) or one of
        // cmd's known descendants. conhost.exe is the console host that
        // wraps cmd; ping.exe is cmd's child. We do NOT want to accept
        // pwsh.exe (the root) or null (no descendant) as a transition.
        var acceptedExes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe",
            "conhost.exe",
            "ping.exe",
        };

        tracker.Changed += (_, e) =>
        {
            lock (observed)
            {
                observed.Add($"{sw.ElapsedMilliseconds}ms pid={e.RootPid} exe={e.ExeBasename ?? "<null>"}");
            }
            if (e.ExeBasename is not null
                && acceptedExes.Contains(e.ExeBasename)
                && !tcs.Task.IsCompleted)
            {
                tcs.SetResult(e.ExeBasename);
            }
        };
        tracker.Register(pwsh!.Id);

        // pwsh.exe startup is heavy (~1-2s cold). Allow up to 8 seconds for
        // the tracker to observe a cmd-tree descendant: pwsh launch + JIT +
        // tick interval 500 ms + debounce 250 ms + slop.
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        lock (observed)
        {
            foreach (var line in observed)
                _output.WriteLine(line);
        }
        Assert.True(
            winner == tcs.Task,
            $"tracker did not report a cmd-tree descendant within 8s; observed: [{string.Join(", ", observed)}]");
        var reported = await tcs.Task;
        Assert.NotNull(reported);
        Assert.Contains(reported!, acceptedExes);
        _output.WriteLine($"tracker reported {reported} after {sw.ElapsedMilliseconds}ms");

        try { pwsh.Kill(entireProcessTree: true); } catch { }
    }
}
