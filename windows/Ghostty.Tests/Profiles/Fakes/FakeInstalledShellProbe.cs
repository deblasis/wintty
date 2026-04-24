using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles.Fakes;

internal sealed class FakeInstalledShellProbe : IInstalledShellProbe
{
    private readonly IReadOnlyList<DiscoveredProfile> _results;
    private readonly bool _throw;

    public FakeInstalledShellProbe(string probeId,
        IReadOnlyList<DiscoveredProfile>? results,
        bool throwOnDiscover = false)
    {
        ProbeId = probeId;
        _results = results ?? System.Array.Empty<DiscoveredProfile>();
        _throw = throwOnDiscover;
    }

    public string ProbeId { get; }

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        CallCount++;
        if (_throw) throw new System.InvalidOperationException("probe intentionally throwing");
        return Task.FromResult(_results);
    }
}
