using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ghostty.IconGen;

/// <summary>
/// Rotates the hue of an icon's saturated pixels, leaving its neutral
/// ones alone.
///
/// The selectivity is the point. The shipping mark is a saturated screen
/// behind a near-white ghost inside a near-neutral silver bezel, so a
/// rotation gated on saturation recolours the screen and its glow while
/// the ghost and the bezel come through untouched. That is what keeps
/// five editions looking like one product rather than five apps, and it
/// means no mask or per-region artwork has to be maintained alongside
/// the masters.
/// </summary>
internal static class TierTint
{
    /// <summary>
    /// Saturation below which a pixel is treated as neutral and left as
    /// it is. The bezel's silver and the ghost's white sit well under
    /// this; the screen and the glow sit well over it. Anti-aliased
    /// pixels along the ghost's edge land in between and move only
    /// partially, which is what stops the edge from banding.
    /// </summary>
    private const double NeutralSaturationCutoff = 0.18;

    public static void Apply(Bitmap bitmap, EditionBrand brand)
    {
        if (brand.HueShiftDegrees == 0 && brand.SaturationScale == 1.0)
            return;

        // Thrown rather than asserted: the branding target runs this
        // tool with -c Release, where Debug.Assert compiles away and a
        // wrong pixel format would silently round-trip through a
        // converted buffer instead of failing the build.
        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            throw new InvalidOperationException(
                "TierTint.Apply expects 32bpp ARGB; the masters load as ARGB.");

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int count = bitmap.Width * bitmap.Height;
            var pixels = new int[count];
            Marshal.Copy(data.Scan0, pixels, 0, count);

            for (int i = 0; i < count; i++)
            {
                int argb = pixels[i];
                int a = (argb >> 24) & 0xFF;
                if (a == 0) continue; // Outside the plate; nothing to recolour.

                int r = (argb >> 16) & 0xFF;
                int g = (argb >> 8) & 0xFF;
                int b = argb & 0xFF;

                RgbToHsl(r, g, b, out double h, out double s, out double l);
                if (s < NeutralSaturationCutoff) continue;

                h = (h + brand.HueShiftDegrees) % 360.0;
                if (h < 0) h += 360.0;
                s = Math.Clamp(s * brand.SaturationScale, 0.0, 1.0);

                HslToRgb(h, s, l, out r, out g, out b);
                pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            Marshal.Copy(pixels, 0, data.Scan0, count);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void RgbToHsl(int r8, int g8, int b8, out double h, out double s, out double l)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        l = (max + min) / 2.0;

        if (delta == 0)
        {
            h = 0;
            s = 0;
            return;
        }

        s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

        if (max == r) h = ((g - b) / delta + (g < b ? 6.0 : 0.0)) * 60.0;
        else if (max == g) h = ((b - r) / delta + 2.0) * 60.0;
        else h = ((r - g) / delta + 4.0) * 60.0;
    }

    private static void HslToRgb(double h, double s, double l, out int r8, out int g8, out int b8)
    {
        if (s == 0)
        {
            r8 = g8 = b8 = (int)Math.Round(l * 255.0);
            return;
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        double hk = h / 360.0;

        r8 = Channel(p, q, hk + 1.0 / 3.0);
        g8 = Channel(p, q, hk);
        b8 = Channel(p, q, hk - 1.0 / 3.0);

        static int Channel(double p, double q, double t)
        {
            if (t < 0) t += 1.0;
            if (t > 1) t -= 1.0;

            double v =
                t < 1.0 / 6.0 ? p + (q - p) * 6.0 * t :
                t < 1.0 / 2.0 ? q :
                t < 2.0 / 3.0 ? p + (q - p) * (2.0 / 3.0 - t) * 6.0 :
                p;

            return (int)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        }
    }
}
