using System.Drawing;
using System.Drawing.Imaging;
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
