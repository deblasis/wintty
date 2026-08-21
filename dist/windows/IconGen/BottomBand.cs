using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ghostty.IconGen;

/// <summary>
/// Owns the bottom band: how tall it is, who gets which part of it, and
/// keeping whatever is drawn there inside the plate's silhouette.
///
/// Applied to each OUTPUT rung rather than to the masters. That is the
/// difference that makes the band legible: painting it on masters and then
/// downsampling left the letters as a smudge at 40, 48 and 60 px, and made
/// the band's own lightness drift across one edition's ladder, ending at
/// 1.4:1 contrast on the two largest rungs - the ones anyone actually looks
/// at. Drawn at final size, every rung gets grid-fit text and the same fill.
/// </summary>
internal static class BottomBand
{
    /// <summary>
    /// Draw whatever this build's channel and edition call for.
    ///
    /// A nightly build of an edition needs both marks. The stripe used to
    /// simply cover the monogram, which was survivable only because the
    /// edition also had a hue; with the hue gone that would leave a nightly
    /// Pro with no edition cue at all. So they split the band: hazard on
    /// top, edition underneath. Nightly still reads first, which is the
    /// ordering that matters - a dev build mistaken for stable is the more
    /// expensive confusion.
    /// </summary>
    public static void Apply(Bitmap bitmap, EditionBrand brand, bool nightly)
    {
        bool hasBand = !string.IsNullOrEmpty(brand.Monogram);
        if (!hasBand && !nightly) return;

        if (bitmap.Width != bitmap.Height)
            throw new InvalidOperationException(
                "BottomBand.Apply expects a square bitmap.");

        int size = bitmap.Width;
        int bandHeight = Math.Max(
            EditionBrand.MinBandPx,
            (int)Math.Round(size * EditionBrand.BandHeightFraction));
        if (bandHeight > size) bandHeight = size;
        int bandTop = size - bandHeight;

        // The plate has rounded corners and the band runs to the bottom
        // edge, so a plain rectangle would square off the two bottom
        // corners. Snapshot the alpha over the whole band, draw everything,
        // then put the alpha back: the result is clipped to whatever
        // silhouette the master actually has, with no second copy of the
        // corner radius to keep in step.
        var alpha = SnapshotAlpha(bitmap, bandTop, bandHeight);

        if (nightly && hasBand)
        {
            int stripeHeight = Math.Max(1, bandHeight / 2);
            HazardStripe.Apply(bitmap, new Rectangle(0, bandTop, size, stripeHeight));
            MonogramBand.Apply(
                bitmap, brand,
                new Rectangle(0, bandTop + stripeHeight, size, bandHeight - stripeHeight));
        }
        else if (nightly)
        {
            HazardStripe.Apply(bitmap, new Rectangle(0, bandTop, size, bandHeight));
        }
        else
        {
            MonogramBand.Apply(bitmap, brand, new Rectangle(0, bandTop, size, bandHeight));
        }

        RestoreAlpha(bitmap, bandTop, bandHeight, alpha);
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
