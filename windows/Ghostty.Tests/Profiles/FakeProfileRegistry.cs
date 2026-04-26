using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles;

internal sealed class FakeProfileRegistry : IProfileRegistry
{
    private List<ResolvedProfile> _profiles = new();

    public IReadOnlyList<ResolvedProfile> Profiles => _profiles;
    public string? DefaultProfileId { get; set; }
    public long Version { get; private set; }

    public event Action<IProfileRegistry>? ProfilesChanged;

    public ResolvedProfile? Resolve(string profileId)
    {
        foreach (var p in _profiles)
            if (p.Id == profileId) return p;
        return null;
    }

    public Task RefreshDiscoveryAsync(CancellationToken ct)
    {
        // Tests drive recomposition via SetProfiles; the refresh
        // entry point is unused at the PR 4 layer.
        return Task.CompletedTask;
    }

    public void Dispose() { }

    /// <summary>
    /// Test-only helper: replace the profile list, bump
    /// <see cref="Version"/>, and fire <see cref="ProfilesChanged"/>
    /// synchronously (mimicking the production UI-dispatch shape from
    /// the perspective of a single-threaded test).
    /// </summary>
    public void SetProfiles(IReadOnlyList<ResolvedProfile> profiles, string? defaultProfileId = null)
    {
        _profiles = new List<ResolvedProfile>(profiles);
        DefaultProfileId = defaultProfileId;
        Version++;
        ProfilesChanged?.Invoke(this);
    }
}
