using System.Drawing;
using System.Drawing.Imaging;
using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.IconGen.Tests;

public class PngWriterTests
{
    [Fact]
    public void WritesAllFourScalePngs()
    {
        using var tempDir = new TempDir();
        using var masters = MasterRasters.Load(TempDir.FindRepoRoot());

        PngWriter.WriteScalePngs(masters, tempDir.Path);

        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-100.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-150.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-200.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "AppIcon.scale-400.png")));
    }

    [Theory]
    [InlineData("AppIcon.scale-100.png", 40)]
    [InlineData("AppIcon.scale-150.png", 60)]
    [InlineData("AppIcon.scale-200.png", 80)]
    [InlineData("AppIcon.scale-400.png", 160)]
    public void EachScalePngHasExpectedDimensions(string fileName, int expectedPx)
    {
        using var tempDir = new TempDir();
        using var masters = MasterRasters.Load(TempDir.FindRepoRoot());

        PngWriter.WriteScalePngs(masters, tempDir.Path);

        using var img = new Bitmap(Path.Combine(tempDir.Path, fileName));
        Assert.Equal(expectedPx, img.Width);
        Assert.Equal(expectedPx, img.Height);
    }

    [Fact]
    public void WritesAllFourSplashPngs()
    {
        using var tempDir = new TempDir();
        using var masters = MasterRasters.Load(TempDir.FindRepoRoot());

        PngWriter.WriteScalePngs(masters, tempDir.Path);

        Assert.True(File.Exists(Path.Combine(tempDir.Path, "SplashIcon.scale-100.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "SplashIcon.scale-150.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "SplashIcon.scale-200.png")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "SplashIcon.scale-400.png")));
    }

    [Theory]
    [InlineData("SplashIcon.scale-100.png", 160)]
    [InlineData("SplashIcon.scale-150.png", 240)]
    [InlineData("SplashIcon.scale-200.png", 320)]
    [InlineData("SplashIcon.scale-400.png", 640)]
    public void EachSplashPngHasExpectedDimensions(string fileName, int expectedPx)
    {
        using var tempDir = new TempDir();
        using var masters = MasterRasters.Load(TempDir.FindRepoRoot());

        PngWriter.WriteScalePngs(masters, tempDir.Path);

        using var img = new Bitmap(Path.Combine(tempDir.Path, fileName));
        Assert.Equal(expectedPx, img.Width);
        Assert.Equal(expectedPx, img.Height);
    }

    // The two theories above pin the names and sizes this tool writes.
    // This pins the shared table it writes them from, so moving
    // LaunchIconMetrics.MaxSizeDips (which the rungs derive from) is a
    // deliberate act with a failing test in front of it rather than a
    // silent regeneration of every asset.
    [Fact]
    public void SharedLadderIsTheScaleAndPixelPairsWeShip()
    {
        var rungs = LaunchIconAssets.Rungs.Select(r => (r.Scale, r.Pixels)).ToArray();

        Assert.Equal(new[] { (100, 160), (150, 240), (200, 320), (400, 640) }, rungs);
    }

    // Ties the output back to the table: every rung the splash window will
    // ask for by name exists, at the size the table promises.
    [Fact]
    public void EverySharedRungIsWrittenAtItsDeclaredSize()
    {
        using var tempDir = new TempDir();
        using var masters = MasterRasters.Load(TempDir.FindRepoRoot());

        PngWriter.WriteScalePngs(masters, tempDir.Path);

        foreach (var rung in LaunchIconAssets.Rungs)
        {
            var path = Path.Combine(tempDir.Path, rung.FileName);
            Assert.True(File.Exists(path), $"IconGen did not write {rung.FileName}");

            using var img = new Bitmap(path);
            Assert.Equal(rung.Pixels, img.Width);
            Assert.Equal(rung.Pixels, img.Height);
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "icongen-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException();
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }
}
