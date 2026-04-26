using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public class ProfileSnapshotStoreFromTests
{
    private static ResolvedProfile Make(string id, string name = "Name") =>
        new(
            Id: id,
            Name: name,
            Command: "cmd.exe",
            WorkingDirectory: null,
            Icon: new IconSpec.BundledKey("default"),
            TabTitle: name,
            Visuals: EffectiveVisualOverrides.Empty,
            ProbeId: null,
            OrderIndex: 0,
            IsDefault: true);

    [Fact]
    public void From_ProducesSnapshotForProfile()
    {
        var profile = Make("foo", "Foo");

        var snapshot = ProfileSnapshotStore.From(profile, version: 7);

        Assert.NotNull(snapshot);
        Assert.Equal("foo", snapshot.ProfileId);
        Assert.Equal(7, snapshot.Version);
        Assert.Equal("cmd.exe", snapshot.ResolvedCommand);
        Assert.Equal("Foo", snapshot.DisplayName);
    }

    [Fact]
    public void Resolve_DelegatesToFrom_ResultEquivalent()
    {
        var profile = Make("foo", "Foo");
        var list = new[] { profile };

        var viaResolve = ProfileSnapshotStore.Resolve("foo", list, version: 3);
        var viaFrom = ProfileSnapshotStore.From(profile, version: 3);

        Assert.NotNull(viaResolve);
        Assert.Equal(viaFrom, viaResolve);
    }
}
