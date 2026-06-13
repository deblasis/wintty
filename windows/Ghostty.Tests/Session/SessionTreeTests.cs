using System.Linq;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

public class SessionTreeTests
{
    private static LeafPane Leaf(string profileId) =>
        new() { Snapshot = Snap(profileId) };

    private static ProfileSnapshot Snap(string id) =>
        new(id, 1, $"{id}.exe", $"C:\\wd\\{id}", id, new IconSpec.BundledKey("default"),
            EffectiveVisualOverrides.Empty);

    [Fact]
    public void CaptureThenRebuild_PreservesStructureRatiosAndProfiles()
    {
        // (a | (b - c)) with non-default ratios
        var inner = new SplitPane(PaneOrientation.Horizontal, Leaf("b"), Leaf("c"), ratio: 0.3);
        var root = new SplitPane(PaneOrientation.Vertical, Leaf("a"), inner, ratio: 0.7);

        var dto = SessionTree.CaptureTree(root);
        var rebuilt = SessionTree.RebuildTree(dto, d => new LeafPane { Snapshot = Snap(d.ProfileId!) });

        var outer = Assert.IsType<SplitPane>(rebuilt);
        Assert.Equal(PaneOrientation.Vertical, outer.Orientation);
        Assert.Equal(0.7, outer.Ratio, precision: 6);
        var inner2 = Assert.IsType<SplitPane>(outer.Child2);
        Assert.Equal(PaneOrientation.Horizontal, inner2.Orientation);
        Assert.Equal(0.3, inner2.Ratio, precision: 6);

        var ids = PaneTree.Leaves(rebuilt).Select(l => l.Snapshot!.ProfileId).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, ids);
    }

    [Fact]
    public void CaptureLeaf_StoresProfileIdAndFallbackCommand()
    {
        var dto = Assert.IsType<LeafDto>(SessionTree.CaptureTree(Leaf("pwsh")));
        Assert.Equal("pwsh", dto.ProfileId);
        Assert.Equal("pwsh.exe", dto.Fallback!.ResolvedCommand);
        Assert.Equal("C:\\wd\\pwsh", dto.Fallback.WorkingDirectory);
        Assert.Equal("pwsh", dto.Fallback.DisplayName);
    }

    [Fact]
    public void CaptureLeaf_NullSnapshot_YieldsNullProfileAndFallback()
    {
        var dto = Assert.IsType<LeafDto>(SessionTree.CaptureTree(new LeafPane()));
        Assert.Null(dto.ProfileId);
        Assert.Null(dto.Fallback);
    }

    [Fact]
    public void PathOf_AndResolve_RoundTrip()
    {
        var b = Leaf("b");
        var inner = new SplitPane(PaneOrientation.Horizontal, Leaf("a"), b, ratio: 0.5);
        var root = new SplitPane(PaneOrientation.Vertical, Leaf("x"), inner, ratio: 0.5);

        var path = SessionTree.PathOf(root, b);
        Assert.Equal(new[] { true, true }, path); // Child2 -> Child2

        Assert.Same(b, SessionTree.Resolve(root, path));
    }

    [Fact]
    public void PathOf_RootLeaf_IsEmpty()
    {
        var only = Leaf("solo");
        Assert.Empty(SessionTree.PathOf(only, only));
        Assert.Same(only, SessionTree.Resolve(only, System.Array.Empty<bool>()));
    }

    [Fact]
    public void Resolve_StalePath_ReturnsNull()
    {
        var root = new SplitPane(PaneOrientation.Vertical, Leaf("a"), Leaf("b"), ratio: 0.5);
        // Path too deep for this tree.
        Assert.Null(SessionTree.Resolve(root, new[] { true, true, true }));
    }
}
