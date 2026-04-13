using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class SnapZoneMathTests
{
    [Fact]
    public void Maximize_returns_input_rect()
    {
        var r = SnapZoneMath.RectFor(SnapZone.Maximize, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(0, 0, 1920, 1080), r);
    }

    [Fact]
    public void LeftHalf_splits_even_width_in_half()
    {
        var r = SnapZoneMath.RectFor(SnapZone.LeftHalf, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(0, 0, 960, 1080), r);
    }

    [Fact]
    public void RightHalf_has_matching_offset_and_remainder_width()
    {
        var r = SnapZoneMath.RectFor(SnapZone.RightHalf, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(960, 0, 960, 1080), r);
    }

    [Fact]
    public void TopHalf_and_BottomHalf_split_height()
    {
        var top = SnapZoneMath.RectFor(SnapZone.TopHalf, 0, 0, 1920, 1080);
        var bot = SnapZoneMath.RectFor(SnapZone.BottomHalf, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(0, 0, 1920, 540), top);
        Assert.Equal(new SnapZoneRect(0, 540, 1920, 540), bot);
    }

    [Fact]
    public void RespectsNonZeroOrigin_including_negative_left()
    {
        // Secondary monitor left of primary, taskbar at top.
        var r = SnapZoneMath.RectFor(SnapZone.LeftHalf, -1920, 100, 1920, 1040);
        Assert.Equal(new SnapZoneRect(-1920, 100, 960, 1040), r);
    }

    [Fact]
    public void OddWidth_rounds_LeftHalf_down_RightHalf_takes_remainder()
    {
        // Width 1921 -> left 960, right 961, right origin at 960. Sum 1921.
        var left = SnapZoneMath.RectFor(SnapZone.LeftHalf, 0, 0, 1921, 1080);
        var right = SnapZoneMath.RectFor(SnapZone.RightHalf, 0, 0, 1921, 1080);
        Assert.Equal(new SnapZoneRect(0, 0, 960, 1080), left);
        Assert.Equal(new SnapZoneRect(960, 0, 961, 1080), right);
        Assert.Equal(1921, left.Width + right.Width);
    }

    [Fact]
    public void TopLeftQuarter_1920x1080()
    {
        var r = SnapZoneMath.RectFor(SnapZone.TopLeftQuarter, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(0, 0, 960, 540), r);
    }

    [Fact]
    public void TopRightQuarter_1920x1080()
    {
        var r = SnapZoneMath.RectFor(SnapZone.TopRightQuarter, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(960, 0, 960, 540), r);
    }

    [Fact]
    public void BottomLeftQuarter_1920x1080()
    {
        var r = SnapZoneMath.RectFor(SnapZone.BottomLeftQuarter, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(0, 540, 960, 540), r);
    }

    [Fact]
    public void BottomRightQuarter_1920x1080()
    {
        var r = SnapZoneMath.RectFor(SnapZone.BottomRightQuarter, 0, 0, 1920, 1080);
        Assert.Equal(new SnapZoneRect(960, 540, 960, 540), r);
    }

    [Fact]
    public void Quarters_odd_dimensions_cover_input_without_seam()
    {
        var tl = SnapZoneMath.RectFor(SnapZone.TopLeftQuarter, 0, 0, 1921, 1081);
        var tr = SnapZoneMath.RectFor(SnapZone.TopRightQuarter, 0, 0, 1921, 1081);
        var bl = SnapZoneMath.RectFor(SnapZone.BottomLeftQuarter, 0, 0, 1921, 1081);
        var br = SnapZoneMath.RectFor(SnapZone.BottomRightQuarter, 0, 0, 1921, 1081);

        // Widths sum to 1921 on top row.
        Assert.Equal(1921, tl.Width + tr.Width);
        // Heights sum to 1081 on left column.
        Assert.Equal(1081, tl.Height + bl.Height);
        // BR starts exactly where TL ends.
        Assert.Equal(tl.X + tl.Width, br.X);
        Assert.Equal(tl.Y + tl.Height, br.Y);
    }

    [Fact]
    public void LeftThird_of_3440_wide()
    {
        var r = SnapZoneMath.RectFor(SnapZone.LeftThird, 0, 0, 3440, 1440);
        // 3440 / 3 = 1146 (int)
        Assert.Equal(new SnapZoneRect(0, 0, 1146, 1440), r);
    }

    [Fact]
    public void MiddleThird_of_3440_wide()
    {
        var r = SnapZoneMath.RectFor(SnapZone.MiddleThird, 0, 0, 3440, 1440);
        Assert.Equal(new SnapZoneRect(1146, 0, 1146, 1440), r);
    }

    [Fact]
    public void RightThird_of_3440_wide_absorbs_remainder()
    {
        var r = SnapZoneMath.RectFor(SnapZone.RightThird, 0, 0, 3440, 1440);
        // Starts at 2*1146 = 2292, width = 3440 - 2292 = 1148.
        Assert.Equal(new SnapZoneRect(2292, 0, 1148, 1440), r);
    }

    [Fact]
    public void Thirds_cover_input_without_seam()
    {
        var l = SnapZoneMath.RectFor(SnapZone.LeftThird, 0, 0, 3440, 1440);
        var m = SnapZoneMath.RectFor(SnapZone.MiddleThird, 0, 0, 3440, 1440);
        var r = SnapZoneMath.RectFor(SnapZone.RightThird, 0, 0, 3440, 1440);
        Assert.Equal(3440, l.Width + m.Width + r.Width);
        Assert.Equal(l.X + l.Width, m.X);
        Assert.Equal(m.X + m.Width, r.X);
    }

    [Fact]
    public void LeftTwoThirds_matches_LeftThird_plus_MiddleThird()
    {
        var lt = SnapZoneMath.RectFor(SnapZone.LeftTwoThirds, 0, 0, 3440, 1440);
        // 2 * (3440/3) = 2292
        Assert.Equal(new SnapZoneRect(0, 0, 2292, 1440), lt);
    }

    [Fact]
    public void RightTwoThirds_matches_MiddleThird_plus_RightThird()
    {
        var rt = SnapZoneMath.RectFor(SnapZone.RightTwoThirds, 0, 0, 3440, 1440);
        // Starts at w/3 = 1146, width = 3440 - 1146 = 2294.
        Assert.Equal(new SnapZoneRect(1146, 0, 2294, 1440), rt);
    }

    [Fact]
    public void TopThird_of_1080x1920_portrait()
    {
        var r = SnapZoneMath.RectFor(SnapZone.TopThird, 0, 0, 1080, 1920);
        // 1920 / 3 = 640
        Assert.Equal(new SnapZoneRect(0, 0, 1080, 640), r);
    }

    [Fact]
    public void MiddleThirdHorizontal_of_1080x1920_portrait()
    {
        var r = SnapZoneMath.RectFor(SnapZone.MiddleThirdHorizontal, 0, 0, 1080, 1920);
        Assert.Equal(new SnapZoneRect(0, 640, 1080, 640), r);
    }

    [Fact]
    public void BottomThird_of_1080x1920_portrait_absorbs_remainder()
    {
        var r = SnapZoneMath.RectFor(SnapZone.BottomThird, 0, 0, 1080, 1920);
        // 2*640 = 1280, remainder = 640
        Assert.Equal(new SnapZoneRect(0, 1280, 1080, 640), r);
    }

    [Fact]
    public void Horizontal_thirds_cover_input_without_seam()
    {
        var t = SnapZoneMath.RectFor(SnapZone.TopThird, 0, 0, 1080, 1921);
        var m = SnapZoneMath.RectFor(SnapZone.MiddleThirdHorizontal, 0, 0, 1080, 1921);
        var b = SnapZoneMath.RectFor(SnapZone.BottomThird, 0, 0, 1080, 1921);
        Assert.Equal(1921, t.Height + m.Height + b.Height);
        Assert.Equal(t.Y + t.Height, m.Y);
        Assert.Equal(m.Y + m.Height, b.Y);
    }
}
