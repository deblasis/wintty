using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerProfileSnapshotTests
{
    private static TabManager NewManager() => new((_) => new FakePaneHost());

    private static ProfileSnapshot SampleSnapshot() =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "foo", Name: "Foo", Command: "cmd.exe",
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("default"),
                TabTitle: "Foo", Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    [Fact]
    public void NewTab_NullSnapshot_LeavesProfileSnapshotNull()
    {
        var mgr = NewManager();
        var tab = mgr.NewTab((ProfileSnapshot?)null);
        Assert.Null(tab.ProfileSnapshot);
    }

    [Fact]
    public void NewTab_WithSnapshot_AttachesSnapshot()
    {
        var mgr = NewManager();
        var snapshot = SampleSnapshot();
        var tab = mgr.NewTab(snapshot);
        Assert.Same(snapshot, tab.ProfileSnapshot);
    }

    [Fact]
    public void NewTab_WithSnapshot_TabAddedSeesSnapshot()
    {
        var mgr = NewManager();
        var snapshot = SampleSnapshot();
        ProfileSnapshot? snapshotAtFire = null;

        mgr.TabAdded += (_, t) => snapshotAtFire = t.ProfileSnapshot;
        mgr.NewTab(snapshot);

        Assert.Same(snapshot, snapshotAtFire);
    }

    [Fact]
    public void NoArgNewTab_DelegatesToNullSnapshotPath()
    {
        var mgr = NewManager();
        var tab = mgr.NewTab();   // existing no-arg surface, unchanged
        Assert.Null(tab.ProfileSnapshot);
    }

    [Fact]
    public void NewTab_WithSnapshot_PassesSnapshotToFactory()
    {
        ProfileSnapshot? capturedSnapshot = null;
        var factoryCallCount = 0;
        var mgr = new TabManager(snap =>
        {
            factoryCallCount++;
            capturedSnapshot = snap;
            return new FakePaneHost();
        });

        // The ctor calls the factory once for the initial tab (null snapshot).
        // Reset so only the NewTab call is measured.
        factoryCallCount = 0;
        capturedSnapshot = null;

        var snapshot = SampleSnapshot();
        mgr.NewTab(snapshot);

        Assert.Equal(1, factoryCallCount);
        Assert.Same(snapshot, capturedSnapshot);
    }
}
