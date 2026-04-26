using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// Probes for pwsh.exe (PowerShell 7+) under
/// %ProgramFiles%\PowerShell\<ver>\pwsh.exe, and for Windows PowerShell
/// (5.1) under %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe.
/// When pwsh is present, runs 'pwsh -v' with a 2-second timeout to
/// capture the version string.
/// </summary>
internal sealed class PowerShellProbe(IFileSystem fs, IProcessRunner runner) : IInstalledShellProbe
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(2);

    public string ProbeId => "powershell";

    public async Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        var list = new List<DiscoveredProfile>();

        var pwsh = FindPwsh();
        if (pwsh is not null)
        {
            var name = await ResolvePwshName(pwsh, ct).ConfigureAwait(false);
            list.Add(new DiscoveredProfile(
                Id: "pwsh-7",
                Name: name,
                Command: ProbeUtil.QuoteIfNeeded(pwsh),
                ProbeId: ProbeId,
                Icon: new IconSpec.BundledKey("pwsh")));
        }

        var winps = FindWindowsPowerShell();
        if (winps is not null)
        {
            list.Add(new DiscoveredProfile(
                Id: "pwsh-windows",
                Name: "Windows PowerShell",
                Command: ProbeUtil.QuoteIfNeeded(winps),
                ProbeId: ProbeId,
                Icon: new IconSpec.BundledKey("powershell")));
        }

        return list;
    }

    private string? FindPwsh()
    {
        var pf = fs.GetKnownFolder(KnownFolderId.ProgramFiles);
        if (pf is null) return null;

        // Well-known subdirs: 7, 7-preview. Iterate deterministically,
        // prefer stable over preview.
        foreach (var sub in new[] { "7", "7-preview" })
        {
            var candidate = Path.Combine(pf, "PowerShell", sub, "pwsh.exe");
            if (fs.FileExists(candidate)) return candidate;
        }
        return null;
    }

    private string? FindWindowsPowerShell()
    {
        var system = fs.GetKnownFolder(KnownFolderId.System);
        if (system is null) return null;
        var p = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return fs.FileExists(p) ? p : null;
    }

    private async Task<string> ResolvePwshName(string exe, CancellationToken ct)
    {
        var result = await runner.RunAsync(exe, new[] { "-v" }, VersionTimeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Stdout.Length == 0) return "PowerShell";
        // Expected format: "PowerShell 7.4.1\n". Trim newline.
        return result.Stdout.Trim();
    }
}
