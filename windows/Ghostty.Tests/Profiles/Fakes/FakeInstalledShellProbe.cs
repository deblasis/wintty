using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles.Fakes;

/// <summary>
/// Returns a fixed list of <see cref="DiscoveredProfile"/>. Tests
/// compose multiple instances of this fake to stand in for the full
/// probe set when exercising <see cref="ProfileOrderResolver"/> or
/// later the <c>DiscoveryService</c>.
/// </summary>
internal sealed class FakeInstalledShellProbe : IInstalledShellProbe
{
    private readonly IReadOnlyList<DiscoveredProfile> _results;

    public FakeInstalledShellProbe(string probeId, IReadOnlyList<DiscoveredProfile> results)
    {
        ProbeId = probeId;
        _results = results;
    }

    public string ProbeId { get; }

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(_results);
}
