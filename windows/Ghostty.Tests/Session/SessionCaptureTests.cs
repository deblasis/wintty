using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

public class SessionCaptureTests
{
    private static LeafPane Leaf(string id) => new()
    {
        Snapshot = new ProfileSnapshot(id, 1, $"{id}.exe", null, id,
            new IconSpec.BundledKey("default"), EffectiveVisualOverrides.Empty),
    };

    [Fact]
    public void CaptureTab_RecordsPathsProfileAndTitle()
    {
        var active = Leaf("b");
        var root = new SplitPane(PaneOrientation.Vertical, Leaf("a"), active, ratio: 0.5);

        var tab = SessionCapture.CaptureTab(root, active, zoomed: active, profileId: "p1", userTitle: "T");

        Assert.Equal("p1", tab.ProfileId);
        Assert.Equal("T", tab.UserTitle);
        Assert.Equal(new[] { true }, tab.ActiveLeafPath);
        Assert.Equal(new[] { true }, tab.ZoomedLeafPath);
        Assert.IsType<SplitDto>(tab.Tree);
    }

    [Fact]
    public void CaptureTab_NoZoom_LeavesZoomPathNull()
    {
        var only = Leaf("solo");
        var tab = SessionCapture.CaptureTab(only, only, zoomed: null, profileId: null, userTitle: null);
        Assert.Null(tab.ZoomedLeafPath);
        Assert.Empty(tab.ActiveLeafPath);
    }
}
