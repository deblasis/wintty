using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Answers "can this environment spawn <em>this</em> executable with
/// redirected pipes, quickly enough to test with?". Some hosts (locked-down
/// sandboxes, aggressive AV, job objects that deny process creation) fail,
/// hang, or take seconds over every spawn from the test host, which makes any
/// spawn-dependent test report a failure of the subject rather than of
/// the machine.
///
/// The executable and the latency budget are per-instance because the answer
/// is not the same for every child. cmd.exe is in-box and starts in
/// milliseconds; pwsh.exe may be absent entirely, and where it is present it
/// pays a runtime cold start that a cmd measurement says nothing about. A
/// test is gated on the probe for the exe it actually spawns.
///
/// The probe deliberately drives System.Diagnostics.Process itself rather
/// than going through IProcessRunner / WindowsProcessRunner. If it used the
/// code under test, a real regression in that code would make the probe
/// fail, the tests would skip, and the regression would ship unnoticed. On
/// an independent path the two causes stay separable: environment broken
/// means skip, product broken means the tests still run and fail.
///
/// Each instance caches its own result for the lifetime of the test host
/// because xunit reads Skip at discovery time, once per test.
/// </summary>
internal sealed class SpawnProbe
{
    /// <summary>
    /// Exit code the probe child is asked to produce. An arbitrary non-zero
    /// value, so "the child really ran our command" cannot be confused with
    /// a process that failed and happened to report 0 or 1.
    /// </summary>
    private const int ExpectedExitCode = 42;

    /// <summary>
    /// Hard cap on one sample, covering the spawn as well as the wait, so a
    /// host where Process.Start itself blocks inside a filter driver cannot
    /// hang discovery with it.
    /// </summary>
    private const int TimeoutMs = 10_000;

    /// <summary>
    /// Latency a host must beat before the tests that spawn cmd.exe mean
    /// anything. A no-op child is milliseconds of work; a host that needs
    /// seconds for it (anti-malware scanning every image, a throttled
    /// sandbox) cannot honour the sub-second and few-second timeouts those
    /// tests assert, so they would fail for the host's reason -- exactly the
    /// confusion this probe removes. Well clear of a healthy runner, which
    /// lands in the low hundreds of ms.
    /// </summary>
    private const int CmdBudgetMs = 2_000;

    /// <summary>
    /// The same question for pwsh.exe, derived from what the tests that spawn
    /// it actually assert: the tracker smoke tests allow themselves 8000 ms
    /// end to end. 750 ms of that is fixed tracker cost (500 ms tick +
    /// 250 ms debounce) and is available to no spawn at all. The remaining
    /// 7250 ms has to cover pwsh getting to the point of running its command
    /// AND everything it is then asked to do -- launch cmd, cmd exec ping,
    /// and a tracker snapshot that catches them alive. This probe measures
    /// only the first half, so it gets half the budget: 3625 ms, taken as
    /// 3500. A host slower than that cannot pass those tests for a reason
    /// that has nothing to do with the tracker.
    /// </summary>
    private const int PwshBudgetMs = 3_500;

    /// <summary>
    /// The diagnosis that fits every verdict about creating or waiting on the
    /// child. It deliberately does not fit "the child exited with the wrong
    /// code": that one observed a process that started fine.
    /// </summary>
    private const string BlockedOrStalling =
        "Process creation from the test host is blocked or stalling "
        + "(sandbox, anti-malware, or job-object limits)";

    /// <summary>
    /// The diagnosis for a start Win32 refused outright, which covers a host
    /// that denies process creation and an executable that is simply not
    /// installed. Nothing observable from here separates the two.
    /// </summary>
    private const string NotFoundOrRefused =
        "That executable is not installed on this host, or process creation "
        + "was refused (sandbox, anti-malware, or job-object limits)";

    /// <summary>
    /// Gate for tests that spawn cmd.exe (or anything else in-box and cheap).
    /// </summary>
    internal static SpawnProbe Cmd { get; } = new(
        "cmd.exe",
        new[] { "/c", $"exit {ExpectedExitCode}" },
        CmdBudgetMs);

    /// <summary>
    /// Gate for tests that spawn pwsh.exe. PowerShell 7 is not in-box on
    /// Windows, so "absent" is a verdict this probe has to reach on its own:
    /// Process.Start throws for a missing image, which is a refused start,
    /// not a timing problem.
    /// </summary>
    internal static SpawnProbe Pwsh { get; } = new(
        "pwsh.exe",
        new[] { "-NoLogo", "-NoProfile", "-Command", $"exit {ExpectedExitCode}" },
        PwshBudgetMs);

    private readonly string _fileName;
    private readonly string[] _args;
    private readonly int _budgetMs;
    private readonly Lazy<string?> _verdict;

    private SpawnProbe(string fileName, string[] args, int budgetMs)
    {
        _fileName = fileName;
        _args = args;
        _budgetMs = budgetMs;
        _verdict = new Lazy<string?>(Decide, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Null when this executable can be spawned here with redirected pipes,
    /// promptly. Otherwise a reason naming what was probed and what the probe
    /// observed, suitable for a CI log.
    /// </summary>
    internal string? Unavailable => _verdict.Value;

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
    private string? Decide()
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

        // Not reachable: a sample is fast, slow-on-timing, or decisive, and a
        // decisive one has already returned. Two samples that did not decide
        // are therefore one fast and one slow, so whichever of the three the
        // third turns out to be completes a pair and returns above.
        throw new UnreachableException(
            $"spawn probe for {_fileName} ran three samples without deciding");
    }

    /// <summary>
    /// One sample, bounded by the hard cap.
    /// </summary>
    /// <remarks>
    /// The sample runs on a worker because Process.Start is itself one of the
    /// things that hangs on the hosts this probe detects -- CreateProcessW
    /// blocks inside an anti-malware filter driver and never returns -- and a
    /// cap that only covered the waits would not cover that. Discovery is
    /// single-file behind an ExecutionAndPublication Lazy, so a sample that
    /// blocks forever would block every other test's discovery too.
    ///
    /// The bool says whether a non-null verdict is about timing: timing
    /// verdicts are worth a second opinion, the rest are not.
    /// </remarks>
    private (string? Verdict, bool IsTiming) Run()
    {
        var sample = Task.Run(Sample);
        // An abandoned worker still finishes eventually, and its failure is
        // not the caller's problem by then. Observe it here so it cannot
        // resurface as an unobserved task exception on the finalizer thread.
        sample.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        try
        {
            if (sample.Wait(TimeoutMs)) return sample.Result;
        }
        catch (Exception ex)
        {
            return (Reason($"threw {ex.GetType().Name}: {ex.Message}", BlockedOrStalling), false);
        }

        return (
            Reason(
                $"had not finished after {TimeoutMs} ms, spawn included",
                BlockedOrStalling),
            false);
    }

    private (string? Verdict, bool IsTiming) Sample()
    {
        try
        {
            // Same shape WindowsProcessRunner uses, because that shape is what
            // fails on the hosts this probe exists to detect: no shell execute,
            // no window, both output pipes redirected, UTF-8 on both, and an
            // environment variable set. That last one is not cosmetic: touching
            // EnvironmentVariables makes .NET hand CreateProcessW an explicit
            // environment block instead of NULL, which is a different Win32
            // path from the one an untouched ProcessStartInfo takes.
            var psi = new ProcessStartInfo
            {
                FileName = _fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["WSL_UTF8"] = "1";
            foreach (var a in _args) psi.ArgumentList.Add(a);

            var sw = Stopwatch.StartNew();
            using var process = Process.Start(psi);
            if (process is null)
                return (Reason("returned no process from Process.Start", BlockedOrStalling), false);

            // Drain before waiting; the reverse order deadlocks if the child
            // ever fills a pipe.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(Remaining(sw)))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Drain(stdout, stderr, sw);
                return (Reason($"had not exited within the {TimeoutMs} ms cap", BlockedOrStalling), false);
            }
            sw.Stop();

            Drain(stdout, stderr, sw);

            if (process.ExitCode != ExpectedExitCode)
                return (
                    Reason(
                        $"exited with {process.ExitCode}, not {ExpectedExitCode}",
                        "Something on this host intercepted or replaced that executable: "
                            + "it started, but it did not run what it was asked to"),
                    false);

            return sw.ElapsedMilliseconds > _budgetMs
                ? (Reason($"took {sw.ElapsedMilliseconds} ms, over the {_budgetMs} ms this host must beat", BlockedOrStalling), true)
                : (null, false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Win32 refused the image. Not necessarily a locked-down host:
            // pwsh.exe is not in-box on Windows, so "absent" arrives here too,
            // and the two are indistinguishable from out here.
            return (Reason($"threw Win32Exception: {ex.Message}", NotFoundOrRefused), false);
        }
        catch (Exception ex)
        {
            return (Reason($"threw {ex.GetType().Name}: {ex.Message}", BlockedOrStalling), false);
        }
    }

    /// <summary>
    /// Waits for the stdout/stderr reads before the caller's
    /// <c>using var process</c> disposes underneath them, so they do not run
    /// as unobserved continuations against a disposed Process. Killing the
    /// child closes the pipes, so after a kill these return at once.
    /// </summary>
    private static void Drain(Task stdout, Task stderr, Stopwatch sw)
    {
        var tasks = new[] { stdout, stderr };
        foreach (var t in tasks)
        {
            t.ContinueWith(
                static x => _ = x.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        try { Task.WaitAll(tasks, Remaining(sw)); } catch { }
    }

    /// <summary>
    /// What is left of the hard cap. The worker inherits the same budget the
    /// caller is waiting on, so an abandoned sample stops shortly after the
    /// caller has given up on it rather than sitting on a live child.
    /// </summary>
    private static int Remaining(Stopwatch sw) =>
        (int)Math.Clamp(TimeoutMs - sw.ElapsedMilliseconds, 0, TimeoutMs);

    private string Reason(string observed, string diagnosis) =>
        $"Spawn probe for `{_fileName} {string.Join(' ', _args)}` "
        + $"(UseShellExecute=false, CreateNoWindow=true, both pipes redirected): "
        + $"it {observed}. {diagnosis}, so tests that spawn {_fileName} would "
        + $"report this host's problem as the subject's.";
}
