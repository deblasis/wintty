using System.Numerics;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The Fluent ladder pins: the strip and switcher motion tokens must sit
/// on the signature-experiences rungs (83 / 167 / 250ms), and every glide
/// must share the table's point-to-point curve. A duration that drifts off
/// a rung -- the switcher's old 150 and 120, or a neighbour of 340's --
/// reads as a second motion system next to every surface that stayed on
/// the ladder; these pins turn that drift into a test failure rather than
/// a slow discovery.
/// </summary>
public class TabMotionFluentRungsTests
{
    [Fact]
    public void FadesAreOnThe83Rung()
        => Assert.Equal(83, TabStripMotion.FadeMs);

    [Fact]
    public void FieldSettleIsOnThe167Rung()
        => Assert.Equal(167, TabStripMotion.FieldSettleMs);

    [Fact]
    public void GapGlideIsOnThe250Rung()
        => Assert.Equal(250, TabStripMotion.GapGlideMs);

    [Fact]
    public void SwitcherHighlightIsOnThe167Rung()
        => Assert.Equal(167, TabSwitcherShape.HighlightMs);

    [Fact]
    public void SwitcherEntranceIsOnThe167Rung()
        => Assert.Equal(167, TabSwitcherShape.EnterMs);

    [Fact]
    public void GlidesShareThePointToPointCurve()
    {
        Assert.Equal(new Vector2(0.55f, 0.55f), TabStripMotion.GlideBezierP1);
        Assert.Equal(new Vector2(0f, 1f), TabStripMotion.GlideBezierP2);
    }
}
