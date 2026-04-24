using System.Collections.Generic;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileSnapshotStoreTests
{
    private static ResolvedProfile Resolved(
        string id,
        string name = "X",
        string command = "x.exe",
        EffectiveVisualOverrides? visuals = null,
        IconSpec? icon = null)
    {
        return new ResolvedProfile(
            Id: id,
            Name: name,
            Command: command,
            WorkingDirectory: null,
            Icon: icon ?? new IconSpec.BundledKey("default"),
            TabTitle: name,
            Visuals: visuals ?? EffectiveVisualOverrides.Empty,
            ProbeId: null,
            OrderIndex: 0,
            IsDefault: false);
    }

    [Fact]
    public void Resolve_ProfileExists_ReturnsSnapshotAtVersion1()
    {
        var resolved = new[] { Resolved("pwsh", name: "PowerShell", command: "pwsh.exe") };

        var snap = ProfileSnapshotStore.Resolve("pwsh", resolved, version: 1);

        Assert.NotNull(snap);
        Assert.Equal("pwsh", snap!.ProfileId);
        Assert.Equal(1, snap.Version);
        Assert.Equal("PowerShell", snap.DisplayName);
        Assert.Equal("pwsh.exe", snap.ResolvedCommand);
    }

    [Fact]
    public void Resolve_ProfileMissing_ReturnsNull()
    {
        var resolved = new[] { Resolved("cmd") };

        var snap = ProfileSnapshotStore.Resolve("ghost", resolved, version: 1);

        Assert.Null(snap);
    }

    [Fact]
    public void Refresh_ProfileFound_ReturnsNewSnapshotAtNewVersion()
    {
        var resolved = new[] { Resolved("pwsh") };
        var snap = ProfileSnapshotStore.Resolve("pwsh", resolved, version: 1)!;

        var refreshed = ProfileSnapshotStore.Refresh(snap, resolved, newVersion: 2);

        Assert.Equal(snap.ProfileId, refreshed.ProfileId);
        Assert.Equal(snap.DisplayName, refreshed.DisplayName);
        Assert.Equal(snap.ResolvedCommand, refreshed.ResolvedCommand);
        Assert.Equal(2, refreshed.Version);
    }

    [Fact]
    public void Refresh_ProfileVisualsChanged_NewSnapshotReflectsThem()
    {
        var resolved = new[] { Resolved("pwsh", visuals: new EffectiveVisualOverrides(Theme: "Light")) };
        var snap = ProfileSnapshotStore.Resolve("pwsh", resolved, version: 1)!;

        var newResolved = new[] { Resolved("pwsh", visuals: new EffectiveVisualOverrides(Theme: "Dark")) };

        var refreshed = ProfileSnapshotStore.Refresh(snap, newResolved, newVersion: 2);

        Assert.Equal("Dark", refreshed.Visuals.Theme);
        Assert.Equal(2, refreshed.Version);
    }

    [Fact]
    public void Refresh_ProfileRemoved_ReturnsExistingSnapshotUnchanged()
    {
        var resolved = new[] { Resolved("pwsh", name: "PowerShell") };
        var snap = ProfileSnapshotStore.Resolve("pwsh", resolved, version: 1)!;

        var refreshed = ProfileSnapshotStore.Refresh(snap, [], newVersion: 2);

        Assert.Same(snap, refreshed);
    }

    [Fact]
    public void Refresh_ProfileRenamed_TreatedAsRemoved()
    {
        var oldResolved = new[] { Resolved("wsl-ubuntu", name: "WSL Ubuntu") };
        var snap = ProfileSnapshotStore.Resolve("wsl-ubuntu", oldResolved, version: 1)!;

        var newResolved = new[] { Resolved("wsl", name: "WSL Ubuntu") };

        var refreshed = ProfileSnapshotStore.Refresh(snap, newResolved, newVersion: 2);

        Assert.Same(snap, refreshed);
        Assert.Equal("wsl-ubuntu", refreshed.ProfileId);
    }
}
