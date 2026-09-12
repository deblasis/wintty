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

    // ShouldDropLeaf carves one exception out of the fallback behaviour
    // pinned above: a leaf whose id resolves to nothing offered AND whose
    // saved fallback cannot be spawned as saved. rc.1 saved Headless SSH
    // tabs exactly so (wintty-release #874 gates the preset off Desktop /
    // Pro Legacy / target-less Enterprise; #823 is the bug), and they
    // restored as local panes running the literal template, re-saving
    // themselves on every launch.
    [Fact]
    public void ShouldDropLeaf_StaleBuiltInWithTemplateCommand_IsDropped()
    {
        var leaf = Leaf("wintty.builtin.headless-ssh", "ssh ${env:WINTTY_SSH_TARGET}");

        Assert.True(SessionProfileResolver.ShouldDropLeaf(new FakeProfileRegistry(), leaf));
    }

    [Fact]
    public void ShouldDropLeaf_WithdrawnBuiltInEvenWithPlainCommand_IsDropped()
    {
        // Post-gate Enterprise saves carry the plain validated command; a
        // target withdrawn later (the gate closing again) must not keep
        // re-spawning it past the gate.
        var leaf = Leaf("wintty.builtin.headless-ssh", "ssh fleet-gw");

        Assert.True(SessionProfileResolver.ShouldDropLeaf(new FakeProfileRegistry(), leaf));
    }

    // THE boundary: the user's own command is spawnable as saved, so a
    // profile that was merely renamed or deleted keeps the fallback.
    [Fact]
    public void ShouldDropLeaf_OrdinaryRenamedCustomProfile_IsKept()
    {
        var leaf = Leaf("my-dev-box", "cmd.exe /k echo hi");

        Assert.False(SessionProfileResolver.ShouldDropLeaf(new FakeProfileRegistry(), leaf));
    }

    [Fact]
    public void ShouldDropLeaf_ResolvingIdIsNeverDropped_EvenWithTemplateCommand()
    {
        // An admin override claiming the built-in id resolves (user wins
        // on id conflicts); the fresh profile is used and the fallback is
        // never consulted.
        var reg = new FakeProfileRegistry();
        reg.Add(Profile("wintty.builtin.headless-ssh", "ssh ${env:WINTTY_SSH_TARGET}"));
        var leaf = Leaf("wintty.builtin.headless-ssh", "ssh ${env:WINTTY_SSH_TARGET}");

        Assert.False(SessionProfileResolver.ShouldDropLeaf(reg, leaf));
    }

    [Fact]
    public void ShouldDropLeaf_LegacyNoProfileLeaf_IsKept()
    {
        Assert.False(SessionProfileResolver.ShouldDropLeaf(
            new FakeProfileRegistry(), Leaf(null, null)));
    }

    [Fact]
    public void ShouldDropLeaf_UnresolvableCustomIdWithTemplateCommand_IsDropped()
    {
        // The dialect rule is general: nothing on any spawn path expands
        // the retired template, so the pane would hand the literal token
        // to its child process whatever profile wrote it.
        var leaf = Leaf("custom", "pwsh -NoProfile -c echo ${env:BUILD_ID}");

        Assert.True(SessionProfileResolver.ShouldDropLeaf(new FakeProfileRegistry(), leaf));
    }

    [Theory]
    [InlineData("WINTTY.BUILTIN.headless-ssh")] // ids compare without case, like the registry
    [InlineData("wintty.builtin.")]             // degenerate bare prefix
    public void ShouldDropLeaf_ReservedBuiltInNamespace_MatchesWithoutCase(string id)
    {
        Assert.True(SessionProfileResolver.ShouldDropLeaf(
            new FakeProfileRegistry(), Leaf(id, "whatever.exe")));
    }

    [Theory]
    [InlineData("wintty.custom-box")]   // near the namespace, not in it
    [InlineData("wintty-builtin.ssh")]  // a dash is not a dot
    public void ShouldDropLeaf_IdsOutsideTheReservedNamespace_AreOrdinary(string id)
    {
        Assert.False(SessionProfileResolver.ShouldDropLeaf(
            new FakeProfileRegistry(), Leaf(id, "whatever.exe")));
    }
}
