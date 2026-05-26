using System.Drawing;
using Xunit;

namespace Ghostty.IconGen.Tests;

public class EndToEndTests
{
    [Fact]
    public void StableProducesIcoAndAllPngs()
    {
        using var tempDir = new TempDir();
        var repoRoot = TempDir.FindRepoRoot();

        int exitCode = Program.Run(
            new[] { "--channel", "stable", "--out", tempDir.Path },
            repoRoot);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "wintty.ico")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "wintty-settings.ico")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-100.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-400.png")));
    }

    [Fact]
    public void SettingsIcoIsRenderedFromGearGlyph()
    {
        // The gear glyph is hollow in the centre by design (a ring
        // with teeth), so a centre-pixel check would fail on the
        // intentional cutout. Instead, count non-transparent pixels
        // in the largest .ico frame and assert coverage is in the
        // band a real gear lands in. Lower bound 0.08 excludes the
        // empty / placeholder-rectangle outline a font without E713
        // would draw (~5-8% area at the 72% canvas fill we use);
        // upper bound 0.35 excludes a solid filled rectangle from a
        // .notdef fallback.
        using var tempDir = new TempDir();
        var repoRoot = TempDir.FindRepoRoot();

        Program.Run(new[] { "--channel", "stable", "--out", tempDir.Path }, repoRoot);

        var icoPath = Path.Combine(tempDir.Path, "wintty-settings.ico");
        Assert.True(File.Exists(icoPath));

        using var img = LoadLargestIcoFrame(icoPath);
        int total = img.Width * img.Height;
        int opaque = CountOpaquePixels(img);
        double coverage = (double)opaque / total;
        Assert.True(coverage is > 0.08 and < 0.35,
            $"Expected gear glyph coverage in [8%, 35%] in wintty-settings.ico; " +
            $"got {coverage:P1}. Likely cause: Segoe Fluent Icons / Segoe MDL2 " +
            $"Assets font missing or wrong glyph code point.");
    }

    private static int CountOpaquePixels(Bitmap bitmap)
    {
        int n = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).A > 0) n++;
        return n;
    }

    private static Bitmap LoadLargestIcoFrame(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        br.ReadUInt16();                  // reserved
        br.ReadUInt16();                  // type
        ushort count = br.ReadUInt16();

        int bestPx = 0;
        uint bestOffset = 0;
        uint bestSize = 0;
        for (int i = 0; i < count; i++)
        {
            byte w = br.ReadByte();
            br.ReadByte();                // height (== width for our writer)
            br.ReadByte();                // palette
            br.ReadByte();                // reserved
            br.ReadUInt16();              // planes
            br.ReadUInt16();              // bpp
            uint size = br.ReadUInt32();
            uint off = br.ReadUInt32();
            int px = w == 0 ? 256 : w;    // 0 means 256 in the .ico spec
            if (px > bestPx)
            {
                bestPx = px;
                bestOffset = off;
                bestSize = size;
            }
        }

        var pngBytes = new byte[bestSize];
        ms.Position = bestOffset;
        ms.ReadExactly(pngBytes);
        using var pngMs = new MemoryStream(pngBytes);
        return new Bitmap(pngMs);
    }

    [Fact]
    public void NightlyPngHasHazardStripe()
    {
        using var tempDir = new TempDir();
        var repoRoot = TempDir.FindRepoRoot();

        Program.Run(new[] { "--channel", "nightly", "--out", tempDir.Path }, repoRoot);

        using var img = new Bitmap(Path.Combine(tempDir.Path, "AppIcon.scale-400.png"));
        // Bottom 15% of 160 px is rows 136..159. Look for yellow pixels.
        int yellowCount = 0;
        for (int y = 136; y < 160; y++)
            for (int x = 0; x < 160; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.R > 200 && c.G > 150 && c.G < 220 && c.B < 80)
                    yellowCount++;
            }
        Assert.True(yellowCount > 50,
            $"Expected yellow stripe pixels in nightly icon; got {yellowCount}.");
    }

    [Fact]
    public void StablePngHasNoYellowStripe()
    {
        using var tempDir = new TempDir();
        var repoRoot = TempDir.FindRepoRoot();

        Program.Run(new[] { "--channel", "stable", "--out", tempDir.Path }, repoRoot);

        using var img = new Bitmap(Path.Combine(tempDir.Path, "AppIcon.scale-400.png"));
        int yellowCount = 0;
        for (int y = 136; y < 160; y++)
            for (int x = 0; x < 160; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.R > 200 && c.G > 150 && c.G < 220 && c.B < 80)
                    yellowCount++;
            }
        Assert.True(yellowCount == 0,
            $"Stable icon should have no yellow stripes; got {yellowCount}.");
    }

    // TODO(icongen): GDI+ antialiasing is not byte-stable across
    // different GDI+ versions shipped with various Windows 10/11
    // builds. Two runs on the same machine produce identical bytes
    // today, but CI on a different host image can drift. If this
    // flakes, either (a) pin the antialiasing in HazardStripe.Apply
    // so the diagonal polygons are deterministic, or (b) hash only
    // the non-stripe region of the icon.
    [Fact]
    public void DeterministicAcrossRuns()
    {
        using var dir1 = new TempDir();
        using var dir2 = new TempDir();
        var repoRoot = TempDir.FindRepoRoot();

        Program.Run(new[] { "--channel", "nightly", "--out", dir1.Path }, repoRoot);
        Program.Run(new[] { "--channel", "nightly", "--out", dir2.Path }, repoRoot);

        var bytes1 = File.ReadAllBytes(Path.Combine(dir1.Path, "wintty.ico"));
        var bytes2 = File.ReadAllBytes(Path.Combine(dir2.Path, "wintty.ico"));
        Assert.Equal(bytes1, bytes2);

        // Same caveat applies to the gear .ico: GDI+ DrawString is
        // deterministic on a given GDI+ build but may drift across
        // Windows versions. Pinning both files catches a regression in
        // either rendering path with one assertion run.
        var settings1 = File.ReadAllBytes(Path.Combine(dir1.Path, "wintty-settings.ico"));
        var settings2 = File.ReadAllBytes(Path.Combine(dir2.Path, "wintty-settings.ico"));
        Assert.Equal(settings1, settings2);
    }
}
