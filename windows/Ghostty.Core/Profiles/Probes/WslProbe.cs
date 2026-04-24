using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// Probes WSL distros via 'wsl --list --quiet'. Each line is a distro
/// name. WindowsProcessRunner sets WSL_UTF8=1 so output is UTF-8; the
/// leading BOM (if any) and stray NUL bytes are stripped defensively.
/// </summary>
internal sealed class WslProbe(IProcessRunner runner) : IInstalledShellProbe
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);

    public string ProbeId => "wsl";

    public async Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync("wsl.exe",
            new[] { "--list", "--quiet" }, ListTimeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return System.Array.Empty<DiscoveredProfile>();

        var profiles = new List<DiscoveredProfile>();
        foreach (var rawLine in result.Stdout.Split('\n'))
        {
            var name = rawLine.Trim('\uFEFF', '\0', ' ', '\t', '\r');
            if (name.Length == 0) continue;

            var id = "wsl-" + Slugify(name);
            profiles.Add(new DiscoveredProfile(
                Id: id,
                Name: $"WSL: {name}",
                Command: $"wsl.exe -d {name}",
                ProbeId: ProbeId,
                Icon: new IconSpec.AutoForWslDistro(name)));
        }
        return profiles;
    }

    private static string Slugify(string name)
    {
        // Lower-case, drop non [a-z0-9-] to keep the ID-format invariant
        // from the spec (see ProfileSourceParser regex).
        var buf = new System.Text.StringBuilder(name.Length);
        foreach (var raw in name.ToLower(CultureInfo.InvariantCulture))
        {
            if (raw is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                buf.Append(raw);
            else if (raw is '_' or ' ')
                buf.Append('-');
        }
        // Collapse duplicate hyphens.
        var s = buf.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }
}
