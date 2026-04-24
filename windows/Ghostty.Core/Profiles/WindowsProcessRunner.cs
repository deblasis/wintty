using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Production IProcessRunner. Wraps System.Diagnostics.Process with a
/// hard timeout and cancellation. Never shells out via cmd; fileName
/// and args are passed verbatim to ProcessStartInfo. Stdout/stderr are
/// captured as UTF-8 strings. WSL_UTF8=1 is set on every spawn so that
/// wsl.exe emits its --list output as UTF-8 rather than UTF-16LE; the
/// variable is ignored by every other exe probes spawn.
/// </summary>
internal sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Force wsl.exe into UTF-8 output. Safe no-op for all other exes.
        psi.EnvironmentVariables["WSL_UTF8"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
                return new ProcessResult(-1, "", "", sw.Elapsed);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // File not found / not executable. Match the interface
            // contract: ExitCode = -1 means "did not start".
            return new ProcessResult(-1, "", "", sw.Elapsed);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // Start reads before WaitForExitAsync; reversing the order can
        // deadlock when the process fills the output pipe before exiting.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            // Drain the stream tasks so they don't run as unobserved continuations
            // against the disposed Process. Kill closes the pipes so these return
            // immediately.
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { }
            return new ProcessResult(-1, "", "", sw.Elapsed);
        }

        // Process exited cleanly; drain the read tasks. If cts was
        // cancelled in the tiny window between WaitForExit returning and
        // the awaits completing (e.g. token raced the exit), treat as a
        // timeout — same shape as the WaitForExit cancellation branch.
        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(-1, "", "", sw.Elapsed);
        }
        return new ProcessResult(process.ExitCode, stdout, stderr, sw.Elapsed);
    }
}
