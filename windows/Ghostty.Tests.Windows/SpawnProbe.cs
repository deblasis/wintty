using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Answers "can this environment spawn a child process with redirected
/// pipes, quickly enough to test with?". Some hosts (locked-down sandboxes,
/// aggressive AV, job objects that deny process creation) fail, hang, or
/// take seconds over every spawn from the test host, which makes any
/// spawn-dependent test report a failure of the subject rather than of
/// the machine.
///
/// The probe deliberately drives System.Diagnostics.Process itself rather
/// than going through IProcessRunner / WindowsProcessRunner. If it used the
/// code under test, a real regression in that code would make the probe
/// fail, the tests would skip, and the regression would ship unnoticed. On
/// an independent path the two causes stay separable: environment broken
/// means skip, product broken means the tests still run and fail.
///
/// The result is cached for the lifetime of the test host because
/// xunit reads Skip at discovery time, once per test.
/// </summary>
internal static class SpawnProbe
{
    /// <summary>
    /// Exit code the probe child is asked to produce. An arbitrary non-zero
    /// value, so "the child really ran our command" cannot be confused with
    /// a process that failed and happened to report 0 or 1.
    /// </summary>
    private const int ExpectedExitCode = 42;

    /// <summary>
    /// Hard cap: past this the probe kills the child and gives up, so a host
    /// where spawning hangs outright cannot hang discovery with it.
    /// </summary>
    private const int TimeoutMs = 10_000;

    /// <summary>
    /// Latency a host must beat for spawn-dependent tests to mean anything.
    /// A no-op child is milliseconds of work; a host that needs seconds for it
    /// (anti-malware scanning every image, a throttled sandbox) cannot honour
    /// the sub-second and few-second timeouts those tests assert, so they would
    /// fail for the host's reason -- exactly the confusion this probe removes.
    /// Well clear of a healthy runner, which lands in the low hundreds of ms.
    /// </summary>
    private const int SlowSpawnMs = 2_000;

    private static readonly Lazy<string?> Probe =
        new(Decide, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Null when a child process with redirected pipes can be spawned here
    /// promptly. Otherwise a reason naming what the probe observed,
    /// suitable for a CI log.
    /// </summary>
    internal static string? Unavailable => Probe.Value;

    /// <summary>
    /// Best of three, decided as soon as two agree.
    /// </summary>
    /// <remarks>
    /// One sample is not enough to answer this. The hosts worth detecting are
    /// not uniformly slow: the same machine has produced a trivial spawn in
    /// tens of ms and, minutes later, in several seconds. A single sample
    /// therefore skips a host that is mostly fine, or clears one that is about
    /// to make the timing assertions fail -- and both of those are the
    /// ambiguity this probe exists to remove.
    ///
    /// Two agreeing samples settle it, so a healthy host pays two trivial
    /// spawns and stops. Anything that is not a timing verdict -- a refused
    /// start, a wrong exit code, no exit at all -- is decisive on its own and
    /// short-circuits, because repeating it would only cost the hard cap
    /// again.
    /// </remarks>
    private static string? Decide()
    {
        string? slow = null;
        var fast = 0;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (verdict, isTiming) = Run();
            if (verdict is not null && !isTiming) return verdict;

            if (verdict is null)
            {
                if (++fast == 2) return null;
            }
            else
            {
                if (slow is not null) return slow;
                slow = verdict;
            }
        }

        // One of each plus a decider that did not match either; the third
        // sample is the tiebreak and it is whatever the loop last saw.
        return fast >= 2 ? null : slow;
    }

    /// <summary>
    /// One sample. The bool says whether a non-null verdict is about timing:
    /// timing verdicts are worth a second opinion, the rest are not.
    /// </summary>
    private static (string? Verdict, bool IsTiming) Run()
    {
        try
        {
            // Same shape WindowsProcessRunner uses, because that shape is what
            // fails on the hosts this probe exists to detect: no shell execute,
            // no window, both output pipes redirected.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"exit {ExpectedExitCode}");

            var sw = Stopwatch.StartNew();
            using var process = Process.Start(psi);
            if (process is null)
                return (Reason("Process.Start returned no process"), false);

            // Drain before waiting; the reverse order deadlocks if the child
            // ever fills a pipe.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (Reason($"the child had not exited after {TimeoutMs} ms"), false);
            }
            sw.Stop();

            try { Task.WaitAll(new Task[] { stdout, stderr }, TimeoutMs); } catch { }

            if (process.ExitCode != ExpectedExitCode)
                return (Reason($"the child exited with {process.ExitCode}"), false);

            return sw.ElapsedMilliseconds > SlowSpawnMs
                ? (Reason($"the child took {sw.ElapsedMilliseconds} ms, over the {SlowSpawnMs} ms this host must beat"), true)
                : (null, false);
        }
        catch (Exception ex)
        {
            return (Reason($"{ex.GetType().Name}: {ex.Message}"), false);
        }
    }

    private static string Reason(string detail) =>
        $"This host cannot usefully spawn child processes with redirected "
        + $"stdout/stderr: probing with `cmd.exe /c exit {ExpectedExitCode}` "
        + $"(UseShellExecute=false, CreateNoWindow=true, both pipes redirected), "
        + $"{detail}. Process creation from the test host is blocked or stalling "
        + $"(sandbox, anti-malware, or job-object limits), so tests that spawn "
        + $"processes would report the host's problem as the subject's.";
}
