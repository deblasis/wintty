using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace Ghostty.SplashGen.Tests;

public class SmokeTest
{
    [Fact]
    public void RunWritesASheetAndSucceeds()
    {
        using var temp = new TempDir();
        var output = Path.Combine(temp.Path, "Splash-Texture.png");

        var exitCode = Program.Run(["--seed", "1", "--out", output], RepoRoot.Find());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
    }

    /// <summary>
    /// The coverage gate is wired to the exit code, not only to the log.
    /// </summary>
    /// <remarks>
    /// Driven with a motif thin enough that the sheet cannot hold the ink,
    /// rather than by reaching into the check. What is being tested is that
    /// a sheet the splash could not use does not leave here reported as a
    /// success, and the only way to see that is to make one.
    /// </remarks>
    [Fact]
    public void ASheetTooThinToCropFails()
    {
        using var temp = new TempDir();
        var motifs = Path.Combine(temp.Path, "motifs");
        Directory.CreateDirectory(motifs);
        WriteHairlineTile(Path.Combine(motifs, "hairline-a.png"));

        var exitCode = Program.Run(
            ["--seed", "1", "--out", Path.Combine(temp.Path, "thin.png"), "--motifs", motifs],
            RepoRoot.Find());

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void BadArgumentsFailWithoutWritingAnything()
    {
        using var temp = new TempDir();

        var exitCode = Program.Run(["--out", Path.Combine(temp.Path, "x.png")], RepoRoot.Find());

        Assert.Equal(1, exitCode);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void AMissingMotifDirectoryFails()
    {
        using var temp = new TempDir();

        var exitCode = Program.Run(
            [
                "--seed", "1",
                "--out", Path.Combine(temp.Path, "x.png"),
                "--motifs", Path.Combine(temp.Path, "nowhere"),
            ],
            RepoRoot.Find());

        Assert.Equal(1, exitCode);
    }

    private static void WriteHairlineTile(string path)
    {
        using var tile = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(tile))
        {
            graphics.Clear(Color.Transparent);
            using var pen = new Pen(Color.White, 2f);
            graphics.DrawLine(pen, 0, 0, 511, 511);
        }

        tile.Save(path, ImageFormat.Png);
    }
}
