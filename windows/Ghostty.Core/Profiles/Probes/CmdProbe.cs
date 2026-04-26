using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// cmd.exe is always part of Windows. This probe only verifies the
/// file exists under KnownFolderId.System; if not, the machine is
/// broken and we silently return empty.
/// </summary>
internal sealed class CmdProbe(IFileSystem fs) : IInstalledShellProbe
{
    public string ProbeId => "cmd";

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        var system = fs.GetKnownFolder(KnownFolderId.System);
        if (system is null)
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());

        var cmd = Path.Combine(system, "cmd.exe");
        if (!fs.FileExists(cmd))
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());

        var profile = new DiscoveredProfile(
            Id: "cmd",
            Name: "Command Prompt",
            Command: ProbeUtil.QuoteIfNeeded(cmd),
            ProbeId: "cmd",
            Icon: new IconSpec.BundledKey("cmd"));

        return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(new[] { profile });
    }
}
