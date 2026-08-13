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

    // Second ladder for the launch icon the shell fades over a cold-start
    // window. That one is painted at 96 DIP, so reusing the 40 DIP ladder
    // above would upscale even its largest rung and look soft. The masters
    // reach 2048 px, so every rung here is still a downsample.
    private static readonly (string Name, int Px)[] SplashTargets =
    {
        ("SplashIcon.scale-100.png", 96),
        ("SplashIcon.scale-150.png", 144),
        ("SplashIcon.scale-200.png", 192),
        ("SplashIcon.scale-400.png", 384),
    };

    public static void WriteScalePngs(MasterRasters masters, string outDir)
    {
        Directory.CreateDirectory(outDir);
        WriteLadder(masters, outDir, ScaleTargets);
        WriteLadder(masters, outDir, SplashTargets);
    }

    private static void WriteLadder(
        MasterRasters masters, string outDir, (string Name, int Px)[] targets)
    {
        foreach (var (name, px) in targets)
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
