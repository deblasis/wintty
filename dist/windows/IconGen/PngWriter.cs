using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Ghostty.Core.Shell;

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

    // Second ladder for the launch icon the shell shows over a cold-start
    // window. Read from the shared table rather than restated here: the
    // splash window loads these files by name at runtime, so a rung this
    // tool renames on its own leaves the shell with nothing to draw. The
    // masters reach 1024 px, so every rung is still a downsample.
    private static readonly (string Name, int Px)[] SplashTargets =
        LaunchIconAssets.Rungs
            .Select(rung => (Name: rung.FileName, Px: rung.Pixels))
            .ToArray();

    public static void WriteScalePngs(
        MasterRasters masters, string outDir, EditionBrand brand, bool nightly)
    {
        Directory.CreateDirectory(outDir);
        WriteLadder(masters, outDir, ScaleTargets, brand, nightly);
        WriteLadder(masters, outDir, SplashTargets, brand, nightly);
    }

    // The PNG encoder, resolved from GDI+ once per process. Image.Save's
    // (path/stream, ImageFormat) overloads re-run the codec lookup on
    // every call, and under signoff-scale load that lookup has come back
    // without the PNG entry (#1096): Save then died far from the cause
    // with ArgumentNullException ('encoder'). One resolution serves every
    // rung of every ladder and every .ico frame, and a GDI+ that
    // genuinely has no PNG encoder fails at the lookup, with the cause
    // in the message instead of an ArgumentNullException inside Save.
    private static readonly Lazy<ImageCodecInfo> PngEncoderLazy = new(
        () => ResolvePngEncoder(ImageCodecInfo.GetImageEncoders()));

    internal static ImageCodecInfo PngEncoder => PngEncoderLazy.Value;

    // Takes the codec enumeration as an argument rather than calling
    // GetImageEncoders itself: that is the seam that lets a test stand in
    // a GDI+ that reports no PNG entry -- the #1096 failure -- without
    // breaking a machine to do it.
    internal static ImageCodecInfo ResolvePngEncoder(
        IEnumerable<ImageCodecInfo> encoders)
    {
        var png = encoders.FirstOrDefault(
            codec => codec.FormatID.Equals(ImageFormat.Png.Guid));
        if (png is null)
            throw new InvalidOperationException(
                "GDI+ reports no PNG encoder in this process, so the icon " +
                "PNGs cannot be written: ImageCodecInfo.GetImageEncoders() " +
                "returned no entry for the PNG format. This has been seen as " +
                "a transient failure under heavy parallel build load (#1096); " +
                "a re-run usually resolves it. If it persists, GDI+ on this " +
                "machine is broken.");
        return png;
    }

    private static void WriteLadder(
        MasterRasters masters, string outDir, (string Name, int Px)[] targets,
        EditionBrand brand, bool nightly)
    {
        var pngEncoder = PngEncoder;
        foreach (var (name, px) in targets)
        {
            using var resized = Resize(masters, px, brand, nightly);
            resized.Save(Path.Combine(outDir, name), pngEncoder, null);
        }
    }

    /// <summary>
    /// Downsample to <paramref name="targetPx"/>, then apply the bottom
    /// band at that size. Banding after the resize is what keeps the
    /// letters grid-fit and the fill identical on every rung; see
    /// <see cref="BottomBand"/>.
    /// </summary>
    public static Bitmap Resize(
        MasterRasters masters, int targetPx, EditionBrand brand, bool nightly)
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

        BottomBand.Apply(output, brand, nightly);
        return output;
    }
}
