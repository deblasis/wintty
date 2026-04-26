using System.Linq;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Ghostty.Tests.Profiles;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class NewTabFlyoutControllerTests
{
    private static ResolvedProfile Make(string id, string name, bool isDefault, int order)
        => new(
            Id: id, Name: name, Command: "cmd.exe",
            WorkingDirectory: null, Icon: new IconSpec.BundledKey("default"),
            TabTitle: name, Visuals: EffectiveVisualOverrides.Empty,
            ProbeId: null, OrderIndex: order, IsDefault: isDefault);

    [Fact]
    public void Items_InitiallyReflectRegistryOrder()
    {
        var registry = new FakeProfileRegistry();
        registry.SetProfiles(new[]
        {
            Make("a", "Alpha", isDefault: false, order: 0),
            Make("b", "Beta",  isDefault: true,  order: 1),
        }, defaultProfileId: "b");

        using var ctrl = new NewTabFlyoutController(registry);

        Assert.Equal(new[] { "a", "b" }, ctrl.Rows.Select(r => r.Id));
        Assert.Equal(new[] { false, true }, ctrl.Rows.Select(r => r.IsDefault));
    }

    [Fact]
    public void ProfilesChanged_RebuildsRows()
    {
        var registry = new FakeProfileRegistry();
        registry.SetProfiles(new[] { Make("a", "Alpha", isDefault: true, order: 0) },
                             defaultProfileId: "a");
        using var ctrl = new NewTabFlyoutController(registry);
        Assert.Single(ctrl.Rows);

        registry.SetProfiles(new[]
        {
            Make("a", "Alpha", isDefault: false, order: 0),
            Make("c", "Charlie", isDefault: true, order: 1),
        }, defaultProfileId: "c");

        Assert.Equal(new[] { "a", "c" }, ctrl.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Disposed_StopsRespondingToProfilesChanged()
    {
        var registry = new FakeProfileRegistry();
        registry.SetProfiles(new[] { Make("a", "Alpha", isDefault: true, order: 0) },
                             defaultProfileId: "a");
        var ctrl = new NewTabFlyoutController(registry);
        ctrl.Dispose();

        registry.SetProfiles(new[]
        {
            Make("a", "Alpha", isDefault: false, order: 0),
            Make("d", "Delta", isDefault: true,  order: 1),
        }, defaultProfileId: "d");

        // Rows still reflect the pre-Dispose snapshot.
        Assert.Single(ctrl.Rows);
        Assert.Equal("a", ctrl.Rows[0].Id);
    }

    [Fact]
    public void Rows_IsDefault_MirrorsResolvedProfile()
    {
        var registry = new FakeProfileRegistry();
        registry.SetProfiles(new[]
        {
            Make("a", "Alpha",   isDefault: false, order: 0),
            Make("b", "Beta",    isDefault: true,  order: 1),
            Make("c", "Charlie", isDefault: false, order: 2),
        }, defaultProfileId: "b");

        using var ctrl = new NewTabFlyoutController(registry);

        Assert.Equal(new[] { false, true, false }, ctrl.Rows.Select(r => r.IsDefault));
    }

    [Fact]
    public void EmptyRegistry_ProducesEmptyRows()
    {
        var registry = new FakeProfileRegistry();
        using var ctrl = new NewTabFlyoutController(registry);
        Assert.Empty(ctrl.Rows);
    }
}
