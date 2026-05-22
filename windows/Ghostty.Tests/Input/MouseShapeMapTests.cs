using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

// Pins MouseShape ordinals to ghostty_action_mouse_shape_e
// (include/ghostty.h:715-750) and the 34-shape -> family mapping
// table in MouseShapeMap.ToFamily.
//
// To re-verify against upstream after a rebase:
//   grep -nE "^  GHOSTTY_MOUSE_SHAPE_" include/ghostty.h
public class MouseShapeMapTests
{
    [Theory]
    // Pin every ordinal to the libghostty C enum value.
    // Order matters: ghostty_action_mouse_shape_e is a plain C enum,
    // so a reorder in mouse.zig silently misroutes shapes if these
    // diverge.
    [InlineData((int)MouseShape.Default,       0)]
    [InlineData((int)MouseShape.ContextMenu,   1)]
    [InlineData((int)MouseShape.Help,          2)]
    [InlineData((int)MouseShape.Pointer,       3)]
    [InlineData((int)MouseShape.Progress,      4)]
    [InlineData((int)MouseShape.Wait,          5)]
    [InlineData((int)MouseShape.Cell,          6)]
    [InlineData((int)MouseShape.Crosshair,     7)]
    [InlineData((int)MouseShape.Text,          8)]
    [InlineData((int)MouseShape.VerticalText,  9)]
    [InlineData((int)MouseShape.Alias,         10)]
    [InlineData((int)MouseShape.Copy,          11)]
    [InlineData((int)MouseShape.Move,          12)]
    [InlineData((int)MouseShape.NoDrop,        13)]
    [InlineData((int)MouseShape.NotAllowed,    14)]
    [InlineData((int)MouseShape.Grab,          15)]
    [InlineData((int)MouseShape.Grabbing,      16)]
    [InlineData((int)MouseShape.AllScroll,     17)]
    [InlineData((int)MouseShape.ColResize,     18)]
    [InlineData((int)MouseShape.RowResize,     19)]
    [InlineData((int)MouseShape.NResize,       20)]
    [InlineData((int)MouseShape.EResize,       21)]
    [InlineData((int)MouseShape.SResize,       22)]
    [InlineData((int)MouseShape.WResize,       23)]
    [InlineData((int)MouseShape.NeResize,      24)]
    [InlineData((int)MouseShape.NwResize,      25)]
    [InlineData((int)MouseShape.SeResize,      26)]
    [InlineData((int)MouseShape.SwResize,      27)]
    [InlineData((int)MouseShape.EwResize,      28)]
    [InlineData((int)MouseShape.NsResize,      29)]
    [InlineData((int)MouseShape.NeswResize,    30)]
    [InlineData((int)MouseShape.NwseResize,    31)]
    [InlineData((int)MouseShape.ZoomIn,        32)]
    [InlineData((int)MouseShape.ZoomOut,       33)]
    public void Ordinal_Matches_Upstream(int actual, int expected)
    {
        Assert.Equal(expected, actual);
    }

    [Theory]
    // Pin every (shape -> family) edge so a future refactor of the
    // mapping table can't silently degrade shapes to Arrow.
    [InlineData(MouseShape.Default,       MouseShapeFamily.Arrow)]
    [InlineData(MouseShape.ContextMenu,   MouseShapeFamily.Arrow)]
    [InlineData(MouseShape.Help,          MouseShapeFamily.Help)]
    [InlineData(MouseShape.Pointer,       MouseShapeFamily.Hand)]
    [InlineData(MouseShape.Progress,      MouseShapeFamily.AppStarting)]
    [InlineData(MouseShape.Wait,          MouseShapeFamily.Wait)]
    [InlineData(MouseShape.Cell,          MouseShapeFamily.Cross)]
    [InlineData(MouseShape.Crosshair,     MouseShapeFamily.Cross)]
    [InlineData(MouseShape.Text,          MouseShapeFamily.IBeam)]
    [InlineData(MouseShape.VerticalText,  MouseShapeFamily.IBeam)]
    [InlineData(MouseShape.Alias,         MouseShapeFamily.Arrow)]
    [InlineData(MouseShape.Copy,          MouseShapeFamily.Arrow)]
    [InlineData(MouseShape.Move,          MouseShapeFamily.SizeAll)]
    [InlineData(MouseShape.NoDrop,        MouseShapeFamily.UniversalNo)]
    [InlineData(MouseShape.NotAllowed,    MouseShapeFamily.UniversalNo)]
    [InlineData(MouseShape.Grab,          MouseShapeFamily.Hand)]
    [InlineData(MouseShape.Grabbing,      MouseShapeFamily.Hand)]
    [InlineData(MouseShape.AllScroll,     MouseShapeFamily.SizeAll)]
    [InlineData(MouseShape.ColResize,     MouseShapeFamily.SizeWestEast)]
    [InlineData(MouseShape.RowResize,     MouseShapeFamily.SizeNorthSouth)]
    [InlineData(MouseShape.NResize,       MouseShapeFamily.SizeNorthSouth)]
    [InlineData(MouseShape.EResize,       MouseShapeFamily.SizeWestEast)]
    [InlineData(MouseShape.SResize,       MouseShapeFamily.SizeNorthSouth)]
    [InlineData(MouseShape.WResize,       MouseShapeFamily.SizeWestEast)]
    [InlineData(MouseShape.NeResize,      MouseShapeFamily.SizeNortheastSouthwest)]
    [InlineData(MouseShape.NwResize,      MouseShapeFamily.SizeNorthwestSoutheast)]
    [InlineData(MouseShape.SeResize,      MouseShapeFamily.SizeNorthwestSoutheast)]
    [InlineData(MouseShape.SwResize,      MouseShapeFamily.SizeNortheastSouthwest)]
    [InlineData(MouseShape.EwResize,      MouseShapeFamily.SizeWestEast)]
    [InlineData(MouseShape.NsResize,      MouseShapeFamily.SizeNorthSouth)]
    [InlineData(MouseShape.NeswResize,    MouseShapeFamily.SizeNortheastSouthwest)]
    [InlineData(MouseShape.NwseResize,    MouseShapeFamily.SizeNorthwestSoutheast)]
    [InlineData(MouseShape.ZoomIn,        MouseShapeFamily.Arrow)]
    [InlineData(MouseShape.ZoomOut,       MouseShapeFamily.Arrow)]
    public void ToFamily_MapsEachShape(MouseShape input, MouseShapeFamily expected)
    {
        Assert.Equal(expected, MouseShapeMap.ToFamily(input));
    }

    [Fact]
    public void ToFamily_UnknownShape_DegradesToArrow()
    {
        // Future-proofing: if libghostty adds shape 34 upstream, we
        // want a safe Arrow fallback rather than a switch-arm KeyNotFound.
        Assert.Equal(MouseShapeFamily.Arrow, MouseShapeMap.ToFamily((MouseShape)99));
    }
}
