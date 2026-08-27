using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Ghostty.SplashGen;

/// <summary>
/// Draws a <see cref="Layout"/> onto a sheet.
/// </summary>
/// <remarks>
/// Nothing here decides anything. Every choice was made by
/// <see cref="SheetLayout"/> and is in the placement list, so a sheet that
/// looks wrong can be diagnosed from the layout without an image, and the
/// layout can be asserted on without a graphics device.
/// </remarks>
public static class SheetRenderer
{
    /// <summary>
    /// How much of a cell's shorter edge a motif is scaled to fill.
    /// </summary>
    /// <remarks>
    /// The tiles came off a 2048 sheet on a five by five grid and are close
    /// to this fraction of that cell already, so at the shipped size this
    /// scale is within a percent of one and the motifs keep their drawn
    /// weight. It exists so that a different grid still produces motifs
    /// that fit their cells rather than motifs that overlap into the next
    /// one.
    /// </remarks>
    public const double MotifFillFraction = 0.86;

    public static Bitmap Render(Layout layout, MotifLibrary library)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(library);

        var sheet = new Bitmap(layout.SheetPixels, layout.SheetPixels, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(sheet))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            var cell = layout.CellPixels;

            foreach (var placement in layout.Placements)
            {
                var tile = library.Image(placement.MotifIndex);
                var scale = MotifFillFraction * cell
                    / Math.Max(tile.Width, tile.Height);
                var width = (float)(tile.Width * scale);
                var height = (float)(tile.Height * scale);

                var state = graphics.Save();
                graphics.TranslateTransform((float)placement.CentreX, (float)placement.CentreY);
                graphics.RotateTransform((float)placement.TurnDegrees);
                graphics.DrawImage(tile, -width / 2f, -height / 2f, width, height);
                graphics.Restore(state);
            }
        }

        FlattenToMask(sheet);
        return sheet;
    }

    /// <summary>
    /// Force every pixel white and leave alpha alone.
    /// </summary>
    /// <remarks>
    /// The splash tints the sheet at draw time and reads alpha as how far
    /// towards the ink a pixel goes, so colour in here is not merely
    /// unused, it is a way for the sheet to stop being a mask without
    /// looking any different in a viewer. Rotating and resampling tiles is
    /// where that would creep in. Doing it once at the end costs one pass
    /// and removes the question.
    /// </remarks>
    private static void FlattenToMask(Bitmap sheet)
    {
        var bounds = new Rectangle(0, 0, sheet.Width, sheet.Height);
        var data = sheet.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * sheet.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            for (int y = 0; y < sheet.Height; y++)
            {
                var row = y * stride;
                for (int x = 0; x < sheet.Width; x++)
                {
                    var i = row + (x * 4);
                    bytes[i] = 255;
                    bytes[i + 1] = 255;
                    bytes[i + 2] = 255;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            sheet.UnlockBits(data);
        }
    }
}
