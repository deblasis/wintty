using System;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Pure rect math for Snap Layouts zones. Integer arithmetic only:
/// work-area coordinates come from DisplayArea.WorkArea as physical
/// pixels and go straight to AppWindow.MoveAndResize, so converting
/// to double and back risks rounding off-by-ones.
///
/// The odd-width split always rounds the left half DOWN and gives
/// the remainder to the right half, keeping left + right equal to
/// the input width with no seam crossing the mid-line.
/// </summary>
internal static class SnapZoneMath
{
    public static SnapZoneRect RectFor(SnapZone zone, int x, int y, int w, int h)
    {
        int halfW = w / 2;
        int halfH = h / 2;
        int restW = w - halfW; // remainder lives in the right/bottom
        int restH = h - halfH;

        int thirdW = w / 3;
        int twoThirdW = 2 * thirdW;
        int restThirdW = w - twoThirdW; // remainder for right third

        int thirdH = h / 3;
        int twoThirdH = 2 * thirdH;
        int restThirdH = h - twoThirdH;

        return zone switch
        {
            SnapZone.Maximize => new SnapZoneRect(x, y, w, h),

            SnapZone.LeftHalf   => new SnapZoneRect(x,         y,         halfW, h),
            SnapZone.RightHalf  => new SnapZoneRect(x + halfW, y,         restW, h),
            SnapZone.TopHalf    => new SnapZoneRect(x,         y,         w,     halfH),
            SnapZone.BottomHalf => new SnapZoneRect(x,         y + halfH, w,     restH),

            SnapZone.TopLeftQuarter     => new SnapZoneRect(x,         y,         halfW, halfH),
            SnapZone.TopRightQuarter    => new SnapZoneRect(x + halfW, y,         restW, halfH),
            SnapZone.BottomLeftQuarter  => new SnapZoneRect(x,         y + halfH, halfW, restH),
            SnapZone.BottomRightQuarter => new SnapZoneRect(x + halfW, y + halfH, restW, restH),

            SnapZone.LeftThird      => new SnapZoneRect(x,              y, thirdW,     h),
            SnapZone.MiddleThird    => new SnapZoneRect(x + thirdW,     y, thirdW,     h),
            SnapZone.RightThird     => new SnapZoneRect(x + twoThirdW,  y, restThirdW, h),
            SnapZone.LeftTwoThirds  => new SnapZoneRect(x,              y, twoThirdW,  h),
            SnapZone.RightTwoThirds => new SnapZoneRect(x + thirdW,     y, w - thirdW, h),

            SnapZone.TopThird              => new SnapZoneRect(x, y,             w, thirdH),
            SnapZone.MiddleThirdHorizontal => new SnapZoneRect(x, y + thirdH,    w, thirdH),
            SnapZone.BottomThird           => new SnapZoneRect(x, y + twoThirdH, w, restThirdH),

            _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "Unhandled zone"),
        };
    }
}
