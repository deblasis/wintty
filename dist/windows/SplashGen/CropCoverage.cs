using System.Drawing;
using System.Drawing.Imaging;
using Ghostty.Core.Shell;

namespace Ghostty.SplashGen;

/// <summary>
/// The thinnest window the splash can take, and how much ink is in it.
/// </summary>
/// <param name="CropPixels">Edge of the square window that was probed.</param>
/// <param name="MinimumInkFraction">
/// The least ink any such window contains, as a fraction of its area.
/// </param>
/// <param name="WorstX">Left edge of the emptiest window found.</param>
/// <param name="WorstY">Top edge of the emptiest window found.</param>
/// <param name="SheetInkFraction">Ink over the whole sheet, for context.</param>
public readonly record struct CoverageReport(
    int CropPixels,
    double MinimumInkFraction,
    int WorstX,
    int WorstY,
    double SheetInkFraction);

/// <summary>
/// Answers the question <see cref="LaunchTexture.MinimumCrop"/> was set to
/// answer: can a crop land between the motifs and come up blank.
/// </summary>
/// <remarks>
/// <para>The floor exists because the sheet leaves about half its cells
/// empty, and a window narrow enough to fit inside the empty ones would
/// show nothing at all. That is a property of the sheet, not of the
/// selector, so it has to be measured on the sheet -- and it moves whenever
/// the grid or the density moves, which is exactly what a generator does.
/// LaunchTexture says as much in its own comment.</para>
///
/// <para>Probed at the floor rather than at the zoom a launch happens to
/// pick, and probed square. The selector takes the floor as the crop's
/// shorter side, so the smallest window it can produce is a square one at
/// exactly that size; anything else it produces contains a square one.
/// Checking the square case therefore settles every case.</para>
///
/// <para>Exhaustive rather than sampled. A summed-area table makes each
/// window four lookups, so every position can be tried instead of a
/// thousand random ones, and the answer is the true worst case rather than
/// the worst of what was drawn. Sampling would have made this test pass on
/// a sheet that has one blank window in it.</para>
/// </remarks>
public static class CropCoverage
{
    /// <summary>
    /// How much ink the thinnest window has to hold to count as textured.
    /// </summary>
    /// <remarks>
    /// The sheet this replaced measures 1.9 percent at its thinnest against
    /// 3.8 percent over the whole sheet, so a floor of one percent is
    /// roughly half of what the shipped asset already managed. It is a
    /// guard against a layout that opened a hole, not a target: a sheet
    /// sitting near this number would be a much emptier sheet than anyone
    /// asked for, and the occupancy report is what says so.
    /// </remarks>
    public const double MinimumInkFraction = 0.01;

    public static CoverageReport Probe(Bitmap sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var width = sheet.Width;
        var height = sheet.Height;
        var crop = (int)Math.Floor(LaunchTexture.MinimumCrop * Math.Min(width, height));
        if (crop < 1) throw new ArgumentException("Sheet is too small to crop", nameof(sheet));

        var table = SummedInk(sheet);
        var span = width + 1;
        var area = (double)crop * crop;

        var worst = double.MaxValue;
        var worstX = 0;
        var worstY = 0;

        for (int y = 0; y + crop <= height; y++)
        {
            var top = y * span;
            var bottom = (y + crop) * span;
            for (int x = 0; x + crop <= width; x++)
            {
                var ink = table[bottom + x + crop]
                    - table[top + x + crop]
                    - table[bottom + x]
                    + table[top + x];

                var fraction = ink / area;
                if (fraction >= worst) continue;

                worst = fraction;
                worstX = x;
                worstY = y;
            }
        }

        var sheetInk = table[(height * span) + width] / ((double)width * height);
        return new CoverageReport(crop, worst, worstX, worstY, sheetInk);
    }

    /// <summary>
    /// Running count of inked pixels above and to the left of each point,
    /// so any window is a difference of four of them.
    /// </summary>
    private static long[] SummedInk(Bitmap sheet)
    {
        var width = sheet.Width;
        var height = sheet.Height;
        var bounds = new Rectangle(0, 0, width, height);
        var data = sheet.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            var span = width + 1;
            var table = new long[span * (height + 1)];

            for (int y = 0; y < height; y++)
            {
                var source = y * stride;
                var previous = y * span;
                var current = (y + 1) * span;
                long rowTotal = 0;

                for (int x = 0; x < width; x++)
                {
                    if (bytes[source + (x * 4) + 3] > 0) rowTotal++;
                    table[current + x + 1] = table[previous + x + 1] + rowTotal;
                }
            }

            return table;
        }
        finally
        {
            sheet.UnlockBits(data);
        }
    }
}
