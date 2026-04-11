using System.Drawing;
using Xunit;

namespace Ghostty.IconGen.Tests;

public class MasterRastersTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void LoadsAllExpectedSizes()
    {
        var masters = MasterRasters.Load(RepoRoot);
        Assert.Contains(16, masters.Sizes);
        Assert.Contains(256, masters.Sizes);
        Assert.Contains(1024, masters.Sizes);
    }

    [Fact]
    public void ReturnsSquareBitmaps()
    {
        var masters = MasterRasters.Load(RepoRoot);
        foreach (var size in masters.Sizes)
        {
            using var bitmap = masters.Get(size);
            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with images/icons not found");
    }
}
