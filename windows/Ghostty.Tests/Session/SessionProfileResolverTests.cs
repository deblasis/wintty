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

    private static LeafDto LeafWithCwd(string? profileId, string? cwd) => new()
    {
        ProfileId = profileId,
        Cwd = cwd,
    };

    private static ResolvedProfile ProfileWithDirectory(string id) =>
        new(id, id, $"{id}.exe", WorkingDirectory: "C:\\profile-wd",
            Icon: new IconSpec.BundledKey("default"), TabTitle: id,
            Visuals: EffectiveVisualOverrides.Empty, ProbeId: null,
            OrderIndex: 0, IsDefault: false);

    [Fact]
    public void ResolveLeaf_ReportedCwd_OverridesTheProfileSDirectory()
    {
        var reg = new FakeProfileRegistry();
        reg.Add(ProfileWithDirectory("pwsh"));

        var snap = SessionProfileResolver.ResolveLeaf(reg, LeafWithCwd("pwsh", "C:\\src"));

        Assert.NotNull(snap);
        // "Same folder" is where the shell actually was, not where the
        // profile's config points.
        Assert.Equal("C:\\src", snap!.WorkingDirectory);
        Assert.Equal("pwsh.exe", snap.ResolvedCommand);
    }

    [Fact]
    public void ResolveLeaf_NeverReportedCwd_KeepsTheProfileSDirectory()
    {
        var reg = new FakeProfileRegistry();
        reg.Add(ProfileWithDirectory("pwsh"));

        Assert.Equal(
            "C:\\profile-wd",
            SessionProfileResolver.ResolveLeaf(reg, LeafWithCwd("pwsh", null))!.WorkingDirectory);
        // Empty is "never reported" too: the surface config treats an
        // empty working-directory as unset, so it must not shadow the
        // profile's value with a string that spawns nowhere.
        Assert.Equal(
            "C:\\profile-wd",
            SessionProfileResolver.ResolveLeaf(reg, LeafWithCwd("pwsh", ""))!.WorkingDirectory);
    }

    [Fact]
    public void ResolveLeaf_ReportedCwd_OverridesTheFallbackCommandSDirectory()
    {
        var reg = new FakeProfileRegistry();
        var leaf = Leaf("deleted", "cmd.exe /k echo hi");
        leaf.Fallback!.WorkingDirectory = "C:\\saved-wd";
        leaf.Cwd = "C:\\moved-on";

        var snap = SessionProfileResolver.ResolveLeaf(reg, leaf);

        Assert.NotNull(snap);
        Assert.Equal("C:\\moved-on", snap!.WorkingDirectory);
        Assert.Equal("cmd.exe /k echo hi", snap.ResolvedCommand);
    }

    // A reported cwd is bytes off the pty, and spawning at one makes Windows
    // authenticate to whatever server it names. Restore reads cwds persisted
    // by builds that predate the check in the terminal core, so this funnel
    // has to refuse them on its own.
    [Theory]
    [InlineData("\\\\evil.example.com\\share")]
    [InlineData("\\\\evil.example.com\\share\\deep")]
    [InlineData("\\\\?\\UNC\\evil.example.com\\share")]
    [InlineData("\\\\?\\unc\\evil.example.com\\share")]
    [InlineData("\\\\.\\COM1")]
    [InlineData("\\\\")]
    // Win32 normalization folds '/' into '\' before it resolves a path, so
    // every separator spelling of a UNC path reaches the same server.
    [InlineData("//evil.example.com/share")]
    [InlineData("\\/evil.example.com/share")]
    [InlineData("/\\evil.example.com/share")]
    // Not an extended-length prefix -- Windows does not normalize inside one --
    // so this is a plain UNC path naming the host "?", which is not local.
    [InlineData("//?/UNC/evil.example.com/share")]
    public void ResolveLeaf_ReportedCwdOnARemoteHost_KeepsTheProfileSDirectory(string cwd)
    {
        var reg = new FakeProfileRegistry();
        reg.Add(ProfileWithDirectory("pwsh"));

        Assert.Equal(
            "C:\\profile-wd",
            SessionProfileResolver.ResolveLeaf(reg, LeafWithCwd("pwsh", cwd))!.WorkingDirectory);
    }

    // ...and the shares that never reach the wire still inherit, so the
    // refusal above is a rule about hosts and not about UNC.
    [Theory]
    [InlineData("\\\\wsl.localhost\\Ubuntu\\home\\alex")]
    [InlineData("\\\\WSL$\\Ubuntu\\home\\alex")]
    [InlineData("\\\\localhost\\c$\\src")]
    [InlineData("\\\\?\\C:\\src")]
    public void ResolveLeaf_ReportedCwdOnALocalShare_StillOverrides(string cwd)
    {
        var reg = new FakeProfileRegistry();
        reg.Add(ProfileWithDirectory("pwsh"));

        Assert.Equal(
            cwd,
            SessionProfileResolver.ResolveLeaf(reg, LeafWithCwd("pwsh", cwd))!.WorkingDirectory);
    }

    [Fact]
    public void ResolveLeaf_ReportedCwdOnThisMachineSOwnShare_StillOverrides()
    {
        var reg = new FakeProfileRegistry();
        reg.Add(ProfileWithDirectory("pwsh"));
        var cwd = $"\\\\{Environment.MachineName}\\c$\\src";

        Assert.Equal(
            cwd,
            SessionProfileResolver.ResolveLeaf(reg, LeafWithCwd("pwsh", cwd))!.WorkingDirectory);
    }
}
