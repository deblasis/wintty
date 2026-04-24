using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// Probes Azure CLI via 'az --version'. If the exit code is zero,
/// register a profile that launches 'az interactive' in the user's
/// current shell. We do not parse the version.
/// </summary>
internal sealed class AzureCloudShellProbe(IProcessRunner runner) : IInstalledShellProbe
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(2);

    public string ProbeId => "azure-cloud-shell";

    public async Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        // Azure CLI ships as az.cmd on Windows; CreateProcessW cannot
        // execute .cmd files directly. Invoke through cmd.exe /c so PATH
        // resolution and the .cmd interpreter both kick in.
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "az", "--version" },
            VersionTimeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return System.Array.Empty<DiscoveredProfile>();

        var profile = new DiscoveredProfile(
            Id: "azure-cloud-shell",
            Name: "Azure Cloud Shell",
            Command: "az interactive",
            ProbeId: ProbeId,
            Icon: new IconSpec.BundledKey("azure"));

        return new[] { profile };
    }
}
