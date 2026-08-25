using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Answers "is wsl installed here, with at least one distribution?", and
/// hands back the distribution name so the gated test does not have to ask
/// again.
///
/// This exists as a gate rather than as an early-out inside the test because
/// an early-out that returns is recorded by xunit as a pass: a test that ran
/// nothing reports the same green as a test that proved something. Skipping
/// says what actually happened.
///
/// Cached for the lifetime of the test host: xunit reads Skip at discovery,
/// once per decorated test.
/// </summary>
internal static class WslDistro
{
    /// <summary>
    /// `wsl --list --quiet` on a host with a distro answers immediately;
    /// anything slower than this is a host problem, and the pwsh spawn probe
    /// that runs before this gate has already ruled that in or out.
    /// </summary>
    private const int TimeoutMs = 2_000;

    /// <summary>
    /// Byte-order mark. <c>wsl --list --quiet</c> emits UTF-16 LE with a BOM
    /// on Windows, so read as the default encoding the stdout carries a
    /// leading mark and interleaved nulls; both are stripped before splitting.
    /// </summary>
    private const char Bom = '\uFEFF';

    private static readonly Lazy<(string? Distro, string? Unavailable)> Result =
        new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Null when a distribution was found. Otherwise a reason for a CI log.
    /// </summary>
    internal static string? Unavailable => Result.Value.Unavailable;

    /// <summary>
    /// The first distribution wsl reported. Non-null whenever
    /// <see cref="Unavailable"/> is null.
    /// </summary>
    internal static string? Name => Result.Value.Distro;

    private static (string? Distro, string? Unavailable) Detect()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "--list --quiet",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return (null, Reason("Process.Start returned no process"));

            // Read before waiting, so a distro list long enough to fill the
            // pipe cannot deadlock the wait.
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { stdout.Wait(TimeoutMs); } catch { }
                return (null, Reason($"it had not answered after {TimeoutMs} ms"));
            }
            string raw;
            try { raw = stdout.Result; }
            catch { raw = ""; }
            if (p.ExitCode != 0)
                return (null, Reason($"it exited with {p.ExitCode}"));

            var cleaned = new string(raw.Where(c => c != '\0' && c != Bom).ToArray());
            var first = cleaned
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            return string.IsNullOrEmpty(first)
                ? (null, Reason("it listed no distributions"))
                : (first, null);
        }
        catch (Exception ex)
        {
            return (null, Reason($"it threw {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static string Reason(string observed) =>
        $"No usable wsl installation: ran `wsl.exe --list --quiet` and {observed}. "
        + $"The spawn probe already cleared this host, so this is wsl's absence "
        + $"rather than a host that cannot start processes.";
}
