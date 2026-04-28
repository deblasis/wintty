using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Ghostty.IconGen;

internal static class PngWriter
{
    // WinUI 3 asset scale -> target pixel size for a 40 DIP icon.
    // Matches the standard WinUI .scale-xxx ladder.
    private static readonly (string Name, int Px)[] ScaleTargets =
    {
        ("AppIcon.scale-100.png", 40),
        ("AppIcon.scale-150.png", 60),
        ("AppIcon.scale-200.png", 80),
        ("AppIcon.scale-400.png", 160),
    };

    public static void WriteScalePngs(MasterRasters masters, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var (name, px) in ScaleTargets)
        {
            using var resized = Resize(masters, px);
            resized.Save(Path.Combine(outDir, name), ImageFormat.Png);
        }
    }

    public static Bitmap Resize(MasterRasters masters, int targetPx)
    {
        // Pick the smallest master >= target for cleanest downsample.
        // If none are large enough, fall back to the largest available
        // and let DrawImage upscale.
        var largeEnough = masters.Sizes.Where(s => s >= targetPx).ToList();
        int sourcePx = largeEnough.Count > 0 ? largeEnough.Min() : masters.Sizes.Max();
        using var source = masters.Get(sourcePx);

        var output = new Bitmap(targetPx, targetPx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, targetPx, targetPx));
        }
        return output;
    }
}
