using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

// Covers the shape -> family mapping table in MouseShapeMap.ToFamily.
//
// The ordinals used to be pinned here as well, against literals copied out of
// MouseShape itself, which could only fail if someone edited one of the two
// copies alone. GhosttyActionTagHeaderParityTests reads
// ghostty_action_mouse_shape_e out of include/ghostty.h instead, in both
// directions, so a reorder and an addition both fail a test.
public class MouseShapeMapTests
{
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
