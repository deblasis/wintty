using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// One discovery probe per installed-shell type (cmd, pwsh, wsl,
/// git-bash, azure-cloud-shell). Probes are pure logic + injected
/// IProcessRunner / IRegistryReader / IFileSystem, so each is a few
/// hundred LOC and fully unit-testable with canned interface inputs.
/// </summary>
public interface IInstalledShellProbe
{
    /// <summary>
    /// Stable, kebab-case identifier for this probe (e.g. "wsl",
    /// "git-bash"). Used as the ProbeId on results and to namespace
    /// generated profile IDs.
    /// </summary>
    string ProbeId { get; }

    Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct);
}
