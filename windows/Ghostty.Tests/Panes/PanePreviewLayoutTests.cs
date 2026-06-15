using System.Linq;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public class PanePreviewLayoutTests
{
    private static PreviewRect Of(LeafPane l,
        System.Collections.Generic.IReadOnlyList<(LeafPane, PreviewRect)> r)
        => r.First(e => ReferenceEquals(e.Item1, l)).Item2;

    [Fact]
    public void Single_leaf_fills_unit_rect()
    {
        var leaf = new LeafPane();
        var rects = PanePreviewLayout.Compute(leaf);
        Assert.Single(rects);
        Assert.Equal(0, rects[0].Item2.X, 3);
        Assert.Equal(0, rects[0].Item2.Y, 3);
        Assert.Equal(1, rects[0].Item2.W, 3);
        Assert.Equal(1, rects[0].Item2.H, 3);
    }

    [Fact]
    public void Vertical_split_places_children_left_and_right()
    {
        var l = new LeafPane();
        var r = new LeafPane();
        var tree = new SplitPane(PaneOrientation.Vertical, l, r, 0.6);
        var rects = PanePreviewLayout.Compute(tree);
        var left = Of(l, rects);
        var right = Of(r, rects);
        Assert.Equal(0, left.X, 3); Assert.Equal(0.6, left.W, 3); Assert.Equal(1, left.H, 3);
        Assert.Equal(0.6, right.X, 3); Assert.Equal(0.4, right.W, 3); Assert.Equal(1, right.H, 3);
    }

    [Fact]
    public void Horizontal_split_places_children_top_and_bottom()
    {
        var t = new LeafPane();
        var b = new LeafPane();
        var tree = new SplitPane(PaneOrientation.Horizontal, t, b, 0.7);
        var rects = PanePreviewLayout.Compute(tree);
        var top = Of(t, rects);
        var bot = Of(b, rects);
        Assert.Equal(0, top.Y, 3); Assert.Equal(0.7, top.H, 3); Assert.Equal(1, top.W, 3);
        Assert.Equal(0.7, bot.Y, 3); Assert.Equal(0.3, bot.H, 3); Assert.Equal(1, bot.W, 3);
    }

    [Fact]
    public void Nested_split_composes_rects()
    {
        var left = new LeafPane();
        var topR = new LeafPane();
        var botR = new LeafPane();
        var tree = new SplitPane(PaneOrientation.Vertical, left,
            new SplitPane(PaneOrientation.Horizontal, topR, botR, 0.5), 0.5);
        var rects = PanePreviewLayout.Compute(tree);
        Assert.Equal(3, rects.Count);
        var tr = Of(topR, rects);
        Assert.Equal(0.5, tr.X, 3); Assert.Equal(0.5, tr.W, 3);
        Assert.Equal(0, tr.Y, 3);   Assert.Equal(0.5, tr.H, 3);
        var br = Of(botR, rects);
        Assert.Equal(0.5, br.X, 3); Assert.Equal(0.5, br.Y, 3); Assert.Equal(0.5, br.H, 3);
    }
}
