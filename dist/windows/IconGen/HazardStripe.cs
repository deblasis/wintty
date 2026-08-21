using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Ghostty.IconGen;

/// <summary>
/// Draws a yellow/black diagonal hazard-stripe band across the bottom
/// 15 percent of a bitmap. Used for nightly/dev build icon variants so
/// Windows dev builds visually align with the GNOME nightly convention
/// (images/gnome/nightly-*.png) without committing any overlay assets.
/// </summary>
internal static class HazardStripe
{
    public static readonly Color StripeYellow = Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18);
    public static readonly Color StripeBlack = Color.FromArgb(0xFF, 0x10, 0x10, 0x10);

    // Band covers the bottom 15 percent of the icon.
    public const double BandHeightFraction = 0.15;

    // Stripe width scales with icon size so the pattern is visible at 16
    // px and not washed out at 1024 px.
    private const double StripesAcrossAt256 = 32;

    /// <summary>
    /// Fill <paramref name="band"/> with the hazard pattern.
    ///
    /// The rectangle is passed in rather than computed here because a
    /// nightly build of an edition splits the bottom band between this and
    /// the edition's own mark; <see cref="BottomBand"/> owns that division.
    /// </summary>
    public static void Apply(Bitmap bitmap, Rectangle band)
    {
        // Stripe-width math below assumes a square canvas. Assert
        // explicitly so a future non-square caller fails loud in Debug
        // instead of silently producing off-axis stripes.
        Debug.Assert(bitmap.Width == bitmap.Height,
            "HazardStripe.Apply expects a square bitmap.");

        int size = bitmap.Width;
        int bandHeight = band.Height;
        int bandTop = band.Y;
        int bandBottom = band.Bottom;

        double stripeWidth = size / StripesAcrossAt256;
        if (stripeWidth < 2) stripeWidth = 2;

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;

        // Fill band with solid black first so gaps between yellow
        // stripes are black rather than showing the base image.
        using (var black = new SolidBrush(StripeBlack))
            g.FillRectangle(black, band);

        // Diagonal yellow stripes at 45 degrees.
        using var yellow = new SolidBrush(StripeYellow);
        double xStart = -size;
        double xEnd = size * 2;
        double step = stripeWidth * 2; // yellow every other stripe

        for (double x = xStart; x < xEnd; x += step)
        {
            var pts = new[]
            {
                new PointF((float)x, bandTop),
                new PointF((float)(x + stripeWidth), bandTop),
                new PointF((float)(x + stripeWidth + bandHeight), bandBottom),
                new PointF((float)(x + bandHeight), bandBottom),
            };
            g.FillPolygon(yellow, pts);
        }
    }
}
