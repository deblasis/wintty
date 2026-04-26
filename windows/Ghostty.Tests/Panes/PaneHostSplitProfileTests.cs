using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Tabs;
using Xunit;

namespace Ghostty.Tests.Panes;

public class PaneHostSplitProfileTests
{
    [Fact]
    public void Split_WithSnapshot_RecordsSnapshot()
    {
        var host = new FakePaneHost();
        var profile = new ResolvedProfile(
            Id: "foo", Name: "Foo", Command: "cmd.exe",
            WorkingDirectory: null, Icon: new IconSpec.BundledKey("default"),
            TabTitle: "Foo", Visuals: EffectiveVisualOverrides.Empty,
            ProbeId: null, OrderIndex: 0, IsDefault: true);
        var snapshot = ProfileSnapshotStore.From(profile, version: 1);

        host.Split(PaneOrientation.Horizontal, snapshot);

        Assert.Equal(1, host.SplitCalls);
        Assert.Equal(PaneOrientation.Horizontal, host.LastSplitOrientation);
        Assert.Same(snapshot, host.LastSplitSnapshot);
    }

    [Fact]
    public void Split_NullSnapshot_PreservesLegacyBehavior()
    {
        var host = new FakePaneHost();
        host.Split(PaneOrientation.Vertical, snapshot: null);

        Assert.Equal(1, host.SplitCalls);
        Assert.Null(host.LastSplitSnapshot);
        // FakePaneHost starts at PaneCount=1; Split increments to 2.
        Assert.Equal(2, host.PaneCount);
    }
}
