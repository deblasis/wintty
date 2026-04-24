using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// Probes Git for Windows via HKLM\SOFTWARE\GitForWindows\InstallPath
/// and verifies bin\bash.exe exists. Command uses --login -i to match
/// the launcher that Git for Windows installs on the Start menu.
/// </summary>
internal sealed class GitBashProbe(IRegistryReader reg, IFileSystem fs) : IInstalledShellProbe
{
    public string ProbeId => "git-bash";

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        var installPath = reg.ReadString(RegistryHive.LocalMachine,
            @"SOFTWARE\GitForWindows", "InstallPath");
        if (string.IsNullOrEmpty(installPath))
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());

        var bash = Path.Combine(installPath, "bin", "bash.exe");
        if (!fs.FileExists(bash))
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());

        var profile = new DiscoveredProfile(
            Id: "git-bash",
            Name: "Git Bash",
            Command: $"{bash} --login -i",
            ProbeId: ProbeId,
            Icon: new IconSpec.BundledKey("git-bash"));

        return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(new[] { profile });
    }
}
