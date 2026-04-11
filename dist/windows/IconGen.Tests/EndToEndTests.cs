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
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "ghostty.ico")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-100.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-400.png")));
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

        var bytes1 = File.ReadAllBytes(Path.Combine(dir1.Path, "ghostty.ico"));
        var bytes2 = File.ReadAllBytes(Path.Combine(dir2.Path, "ghostty.ico"));
        Assert.Equal(bytes1, bytes2);
    }
}
