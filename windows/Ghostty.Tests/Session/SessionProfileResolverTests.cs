using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

public class SessionProfileResolverTests
{
    private sealed class FakeProfileRegistry : IProfileRegistry
    {
        private readonly Dictionary<string, ResolvedProfile> _byId = new();
        public long Version { get; } = 7;
        public IReadOnlyList<ResolvedProfile> Profiles => new List<ResolvedProfile>(_byId.Values);
        public IReadOnlyList<ResolvedProfile> HiddenProfiles => Array.Empty<ResolvedProfile>();
        public string? DefaultProfileId { get; set; }
        public event Action<IProfileRegistry>? ProfilesChanged { add { } remove { } }

        public void Add(ResolvedProfile p) => _byId[p.Id] = p;
        public ResolvedProfile? Resolve(string profileId) =>
            _byId.TryGetValue(profileId, out var p) ? p : null;
        public Task RefreshDiscoveryAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static ResolvedProfile Profile(string id, string command) =>
        new(id, id, command, WorkingDirectory: null, Icon: new IconSpec.BundledKey("default"),
            TabTitle: id, Visuals: EffectiveVisualOverrides.Empty, ProbeId: null,
            OrderIndex: 0, IsDefault: false);

    private static LeafDto Leaf(string? profileId, string? fallbackCommand) => new()
    {
        ProfileId = profileId,
        Fallback = fallbackCommand is null ? null
            : new LeafCommand { ResolvedCommand = fallbackCommand, DisplayName = "fb" },
    };

    [Fact]
    public void ResolveById_ExistingProfile_ResolvesFresh()
    {
        var reg = new FakeProfileRegistry();
        reg.Add(Profile("pwsh", "pwsh.exe"));

        var snap = SessionProfileResolver.ResolveById(reg, "pwsh");

        Assert.NotNull(snap);
        Assert.Equal("pwsh", snap!.ProfileId);
        Assert.Equal("pwsh.exe", snap.ResolvedCommand);
        Assert.Equal(7, snap.Version);
    }

    [Fact]
    public void ResolveById_RemovedProfile_ReturnsNull_NoDefaultSubstitution()
    {
        var reg = new FakeProfileRegistry { DefaultProfileId = "pwsh" };
        reg.Add(Profile("pwsh", "pwsh.exe"));

        // "gone" no longer resolves; we must NOT silently substitute the default.
        Assert.Null(SessionProfileResolver.ResolveById(reg, "gone"));
    }

    [Fact]
    public void ResolveById_NullIdOrRegistry_ReturnsNull()
    {
        Assert.Null(SessionProfileResolver.ResolveById(new FakeProfileRegistry(), null));
        Assert.Null(SessionProfileResolver.ResolveById(null, "pwsh"));
    }

    [Fact]
    public void ResolveDefault_UsesDefaultProfileId()
    {
        var reg = new FakeProfileRegistry { DefaultProfileId = "pwsh" };
        reg.Add(Profile("cmd", "cmd.exe"));
        reg.Add(Profile("pwsh", "pwsh.exe"));

        var snap = SessionProfileResolver.ResolveDefault(reg);

        Assert.NotNull(snap);
        Assert.Equal("pwsh", snap!.ProfileId);
        Assert.Equal("pwsh.exe", snap.ResolvedCommand);
    }

    [Fact]
    public void ResolveDefault_MissingDefault_ReturnsNull()
    {
        var reg = new FakeProfileRegistry { DefaultProfileId = "gone" };
        Assert.Null(SessionProfileResolver.ResolveDefault(reg));
        Assert.Null(SessionProfileResolver.ResolveDefault(null));
        Assert.Null(SessionProfileResolver.ResolveDefault(new FakeProfileRegistry()));
    }

    [Fact]
    public void ResolveLeaf_RemovedProfile_FallsBackToSavedCommand_NotDefault()
    {
        var reg = new FakeProfileRegistry { DefaultProfileId = "pwsh" };
        reg.Add(Profile("pwsh", "pwsh.exe"));

        // Leaf ran a since-deleted profile; it must re-run its saved command,
        // not the user's default shell.
        var snap = SessionProfileResolver.ResolveLeaf(reg, Leaf("deleted", "cmd.exe /k echo hi"));

        Assert.NotNull(snap);
        Assert.Equal("cmd.exe /k echo hi", snap!.ResolvedCommand);
    }

    [Fact]
    public void ResolveLeaf_ExistingProfile_PrefersFreshOverFallback()
    {
        var reg = new FakeProfileRegistry();
        reg.Add(Profile("pwsh", "pwsh.exe"));

        var snap = SessionProfileResolver.ResolveLeaf(reg, Leaf("pwsh", "stale.exe"));

        Assert.Equal("pwsh.exe", snap!.ResolvedCommand);
    }

    [Fact]
    public void ResolveLeaf_NoProfileNoFallback_ReturnsNull()
    {
        Assert.Null(SessionProfileResolver.ResolveLeaf(new FakeProfileRegistry(), Leaf(null, null)));
    }
}
