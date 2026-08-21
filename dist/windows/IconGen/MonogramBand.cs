using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Ghostty.IconGen;

/// <summary>
/// Draws the edition monogram across the bottom band of an icon.
///
/// The band is held to <see cref="EditionBrand.BandHeightFraction"/> so
/// it stays below the mark; see that constant for the measurement it
/// comes from. Below <see cref="EditionBrand.MinLegibleBandPx"/> the
/// letters are dropped and a plain bar is drawn instead, because a
/// three-glyph monogram rendered into four pixels is not a small
/// monogram, it is a smudge that reads as a rendering fault.
/// </summary>
internal static class MonogramBand
{
    private static readonly Color BandInk = Color.FromArgb(0xFF, 0x12, 0x16, 0x1B);

    public static void Apply(Bitmap bitmap, EditionBrand brand)
    {
        if (string.IsNullOrEmpty(brand.Monogram)) return;

        // Thrown rather than asserted: the branding target runs this
        // tool with -c Release, where Debug.Assert compiles away and a
        // wrong pixel format would silently round-trip through a
        // converted buffer instead of failing the build.
        if (bitmap.Width != bitmap.Height)
            throw new InvalidOperationException(
                "MonogramBand.Apply expects a square bitmap.");

        int size = bitmap.Width;
        int bandHeight = (int)Math.Round(size * EditionBrand.BandHeightFraction);
        if (bandHeight < 2) bandHeight = 2;
        int bandTop = size - bandHeight;

        // The band's colour is the edition's own hue, taken from the icon
        // it is being drawn on rather than from a second table that could
        // drift away from the tint.
        var bandColour = SampleScreenColour(bitmap, brand);

        // The plate has rounded corners and the band runs to the bottom
        // edge, so a plain rectangle would square off the two bottom
        // corners. Snapshot the alpha, draw, then put the alpha back:
        // the band ends up clipped to whatever silhouette the master
        // actually has, with no second copy of the corner radius to keep
        // in step.
        var alpha = SnapshotAlpha(bitmap, bandTop, bandHeight);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (var brush = new SolidBrush(bandColour))
                g.FillRectangle(brush, 0, bandTop, size, bandHeight);

            // A hairline along the top edge so the band reads as a
            // deliberate layer rather than the screen changing colour
            // halfway down.
            int rule = Math.Max(1, (int)Math.Round(size / 256.0 * 4));
            using (var shade = new SolidBrush(Color.FromArgb(0x40, 0, 0, 0)))
                g.FillRectangle(shade, 0, bandTop, size, rule);

            if (bandHeight >= EditionBrand.MinLegibleBandPx)
                DrawMonogram(g, brand.Monogram, size, bandTop, bandHeight);
        }

        RestoreAlpha(bitmap, bandTop, bandHeight, alpha);
    }

    private static void DrawMonogram(Graphics g, string text, int size, int bandTop, int bandHeight)
    {
        // Fitted to the band rather than to a fixed point size, so the
        // monogram occupies the same proportion of every master instead
        // of drifting between 32 px and 1024 px.
        float emSize = bandHeight * 0.72f;

        using var font = new Font(
            FontFamily.GenericSansSerif, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(BandInk);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        var layout = new RectangleF(0, bandTop, size, bandHeight);
        g.DrawString(text, font, brush, layout, format);
    }

    /// <summary>
    /// The band's fill, derived from the icon's own screen so it tracks
    /// whatever <see cref="TierTint"/> did. Sampled from a row above the
    /// band and lightened, which keeps the band related to the screen
    /// while staying separable from it.
    /// </summary>
    private static Color SampleScreenColour(Bitmap bitmap, EditionBrand brand)
    {
        int size = bitmap.Width;
        int y = (int)Math.Round(size * 0.62);
        int x = (int)Math.Round(size * 0.5);
        var sampled = bitmap.GetPixel(Math.Clamp(x, 0, size - 1), Math.Clamp(y, 0, size - 1));

        // A fully desaturated edition would otherwise get a grey band on
        // a grey screen; nudge the lightness so the edge survives.
        int lift = brand.SaturationScale < 0.3 ? 70 : 46;
        return Color.FromArgb(
            0xFF,
            Math.Clamp(sampled.R + lift, 0, 255),
            Math.Clamp(sampled.G + lift, 0, 255),
            Math.Clamp(sampled.B + lift, 0, 255));
    }

    private static byte[] SnapshotAlpha(Bitmap bitmap, int bandTop, int bandHeight)
    {
        var rect = new Rectangle(0, bandTop, bitmap.Width, bandHeight);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int count = bitmap.Width * bandHeight;
            var pixels = new int[count];
            Marshal.Copy(data.Scan0, pixels, 0, count);

            var alpha = new byte[count];
            for (int i = 0; i < count; i++)
                alpha[i] = (byte)((pixels[i] >> 24) & 0xFF);
            return alpha;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void RestoreAlpha(Bitmap bitmap, int bandTop, int bandHeight, byte[] alpha)
    {
        var rect = new Rectangle(0, bandTop, bitmap.Width, bandHeight);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int count = bitmap.Width * bandHeight;
            var pixels = new int[count];
            Marshal.Copy(data.Scan0, pixels, 0, count);

            for (int i = 0; i < count; i++)
            {
                int drawn = (pixels[i] >> 24) & 0xFF;
                int keep = Math.Min(drawn, alpha[i]);
                pixels[i] = (keep << 24) | (pixels[i] & 0x00FFFFFF);
            }

            Marshal.Copy(pixels, 0, data.Scan0, count);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
