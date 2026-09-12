using System;
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
    public void CaptureLeaf_CarriesThePaneSLastReportedDirectory()
    {
        var leaf = Leaf("pwsh");
        leaf.LastCwd = "C:\\src";

        var dto = Assert.IsType<LeafDto>(SessionTree.CaptureTree(leaf));

        Assert.Equal("C:\\src", dto.Cwd);
    }

    [Fact]
    public void CaptureLeaf_NeverReportedDirectory_StaysNull()
    {
        var dto = Assert.IsType<LeafDto>(SessionTree.CaptureTree(Leaf("pwsh")));

        Assert.Null(dto.Cwd);
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

    [Fact]
    public void Resolve_PartialPath_ReturnsInteriorSplit()
    {
        // A path that stops above a leaf resolves to a SplitPane, not a leaf.
        // SessionRestorer relies on `Resolve(...) as LeafPane ?? FirstLeaf`,
        // so this must be a non-leaf node (cast yields null -> fallback).
        var inner = new SplitPane(PaneOrientation.Horizontal, Leaf("a"), Leaf("b"), ratio: 0.5);
        var root = new SplitPane(PaneOrientation.Vertical, Leaf("x"), inner, ratio: 0.5);

        var node = SessionTree.Resolve(root, new[] { true }); // -> inner split
        Assert.IsType<SplitPane>(node);
        Assert.Null(node as LeafPane);
    }

    [Theory]
    [InlineData(1.5, SplitPane.MaxRatio)]
    [InlineData(-0.2, SplitPane.MinRatio)]
    [InlineData(0.99, SplitPane.MaxRatio)]
    public void RebuildTree_ClampsOutOfRangeRatio(double persisted, double expected)
    {
        var dto = new SplitDto
        {
            Orientation = PaneOrientation.Vertical,
            Ratio = persisted,
            Child1 = new LeafDto { ProfileId = "a" },
            Child2 = new LeafDto { ProfileId = "b" },
        };

        var rebuilt = (SplitPane)SessionTree.RebuildTree(dto, d => new LeafPane { Snapshot = Snap(d.ProfileId!) });
        Assert.Equal(expected, rebuilt.Ratio, precision: 6);
    }

    // makeLeaf returning null is a refusal: restore drops the leaf (a
    // saved pane whose profile is gone AND whose fallback cannot be
    // spawned). A split it empties collapses; a tree with nothing left
    // refuses in turn so the caller can drop the tab.
    private static LeafPane? KeepAllBut(LeafDto d) =>
        d.ProfileId!.StartsWith("refuse", StringComparison.Ordinal)
            ? null
            : new LeafPane { Snapshot = Snap(d.ProfileId!) };

    [Fact]
    public void RebuildTree_RefusedLeaf_CollapsesItsSplit()
    {
        var dto = new SplitDto
        {
            Orientation = PaneOrientation.Vertical,
            Child1 = new LeafDto { ProfileId = "keep" },
            Child2 = new LeafDto { ProfileId = "refuse" },
        };

        var rebuilt = SessionTree.RebuildTree(dto, KeepAllBut);

        // No one-pane split: the survivor IS the root now.
        var leaf = Assert.IsType<LeafPane>(rebuilt);
        Assert.Equal("keep", leaf.Snapshot!.ProfileId);
    }

    [Fact]
    public void RebuildTree_NestedRefusals_PromoteTheSurvivor()
    {
        // (refuse - keep) - refuse-too: both splits lose a side.
        var dto = new SplitDto
        {
            Orientation = PaneOrientation.Vertical,
            Child1 = new SplitDto
            {
                Orientation = PaneOrientation.Horizontal,
                Child1 = new LeafDto { ProfileId = "refuse" },
                Child2 = new LeafDto { ProfileId = "keep" },
            },
            Child2 = new LeafDto { ProfileId = "refuse-too" },
        };

        var rebuilt = SessionTree.RebuildTree(dto, KeepAllBut);

        var leaf = Assert.IsType<LeafPane>(rebuilt);
        Assert.Equal("keep", leaf.Snapshot!.ProfileId);
    }

    [Fact]
    public void RebuildTree_TwoSurvivors_KeepTheirSplit()
    {
        // keep - (refuse - keep): the outer split keeps both sides, the
        // inner collapses. Refusals must never take a live split down.
        var dto = new SplitDto
        {
            Orientation = PaneOrientation.Vertical,
            Ratio = 0.7,
            Child1 = new LeafDto { ProfileId = "keep-a" },
            Child2 = new SplitDto
            {
                Orientation = PaneOrientation.Horizontal,
                Child1 = new LeafDto { ProfileId = "refuse" },
                Child2 = new LeafDto { ProfileId = "keep-b" },
            },
        };

        var rebuilt = Assert.IsType<SplitPane>(SessionTree.RebuildTree(dto, KeepAllBut));

        Assert.Equal(0.7, rebuilt.Ratio, precision: 6);
        var a = Assert.IsType<LeafPane>(rebuilt.Child1);
        var b = Assert.IsType<LeafPane>(rebuilt.Child2);
        Assert.Equal("keep-a", a.Snapshot!.ProfileId);
        Assert.Equal("keep-b", b.Snapshot!.ProfileId);
    }

    [Fact]
    public void RebuildTree_AllLeavesRefused_ReturnsNull()
    {
        LeafPane? Refuse(LeafDto _) => null;

        Assert.Null(SessionTree.RebuildTree(new LeafDto { ProfileId = "x" }, Refuse));
        Assert.Null(SessionTree.RebuildTree(new SplitDto
        {
            Orientation = PaneOrientation.Vertical,
            Child1 = new LeafDto { ProfileId = "x" },
            Child2 = new LeafDto { ProfileId = "y" },
        }, Refuse));
    }

    [Fact]
    public void RebuildTree_ResolverDrivenDrop_RemovesTheStaleLeafFromTheNextSave()
    {
        // The restore the way SessionRestorer drives it: refuse exactly
        // what SessionProfileResolver.ShouldDropLeaf refuses, spawn the
        // rest through ResolveLeaf. A stale rc.1 Headless SSH leaf
        // beside an ordinary custom leaf, on a machine where nothing
        // resolves (Desktop: the preset is not offered at all).
        IProfileRegistry? registry = null;
        var dto = new SplitDto
        {
            Orientation = PaneOrientation.Horizontal,
            Child1 = new LeafDto
            {
                ProfileId = "wintty.builtin.headless-ssh",
                Fallback = new LeafCommand
                {
                    ResolvedCommand = "ssh ${env:WINTTY_SSH_TARGET}",
                    DisplayName = "Headless SSH",
                },
            },
            Child2 = new LeafDto
            {
                ProfileId = "my-dev-box",
                Fallback = new LeafCommand
                {
                    ResolvedCommand = "cmd.exe /k echo hi",
                    DisplayName = "dev",
                },
            },
        };

        var rebuilt = SessionTree.RebuildTree(dto, leaf =>
            SessionProfileResolver.ShouldDropLeaf(registry, leaf)
                ? null
                : new LeafPane { Snapshot = SessionProfileResolver.ResolveLeaf(registry, leaf) });

        var survivor = Assert.IsType<LeafPane>(rebuilt);
        Assert.Equal("my-dev-box", survivor.Snapshot!.ProfileId);
        Assert.Equal("cmd.exe /k echo hi", survivor.Snapshot.ResolvedCommand);

        // The save after that restore cannot resurrect the leaf: the
        // rebuilt tree, captured back, carries only the survivor.
        var resaved = Assert.IsType<LeafDto>(SessionTree.CaptureTree(rebuilt));
        Assert.Equal("my-dev-box", resaved.ProfileId);
    }

    [Fact]
    public void RebuildTree_ResolverDrivenKeep_OrdinaryOneLinerSurvives()
    {
        // The same driven shape, keep side: an unresolvable custom
        // profile whose command embeds ${env:BUILD_ID} is the user's
        // own live PowerShell, not the retired template, so restore,
        // duplicate and reopen all rebuild it with its command intact.
        IProfileRegistry? registry = null;
        var dto = new LeafDto
        {
            ProfileId = "custom",
            Fallback = new LeafCommand
            {
                ResolvedCommand = "pwsh -NoProfile -c echo ${env:BUILD_ID}",
                DisplayName = "build",
            },
        };

        var rebuilt = SessionTree.RebuildTree(dto, leaf =>
            SessionProfileResolver.ShouldDropLeaf(registry, leaf)
                ? null
                : new LeafPane { Snapshot = SessionProfileResolver.ResolveLeaf(registry, leaf) });

        var survivor = Assert.IsType<LeafPane>(rebuilt);
        Assert.Equal("custom", survivor.Snapshot!.ProfileId);
        Assert.Equal("pwsh -NoProfile -c echo ${env:BUILD_ID}",
            survivor.Snapshot.ResolvedCommand);
    }
}
