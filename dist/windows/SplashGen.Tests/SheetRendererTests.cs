using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace Ghostty.SplashGen.Tests;

public class SheetRendererTests
{
    private const int SheetPixels = 2048;
    private const int GridCells = 5;

    /// <summary>
    /// Same seed, byte-identical PNG. Determinism at the layout level is
    /// not enough to promise a reproducible asset: everything between a
    /// placement list and a file -- rotation, resampling, the encoder --
    /// has to land in the same place too, or regenerating the sheet
    /// produces a diff nobody can explain.
    /// </summary>
    [Fact]
    public void SameSeedWritesTheSameBytes()
    {
        var first = RenderToPng(seed: 20260827);
        var second = RenderToPng(seed: 20260827);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeedsWriteDifferentBytes()
    {
        Assert.NotEqual(RenderToPng(seed: 1), RenderToPng(seed: 2));
    }

    /// <summary>
    /// The sheet is a mask: white everywhere, with the mark carried in
    /// alpha.
    /// </summary>
    /// <remarks>
    /// The splash tints it at draw time and reads alpha as how far towards
    /// the ink a pixel goes. Colour that crept in from resampling would not
    /// look wrong in a viewer and would not throw anywhere; it would just
    /// quietly stop the tint from being the only thing that decides the
    /// texture's colour.
    /// </remarks>
    [Fact]
    public void EveryPixelIsWhiteAndAlphaCarriesTheMark()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));
        var layout = SheetLayout.Build(9, SheetPixels, GridCells, library.Motifs);
        using var sheet = SheetRenderer.Render(layout, library);

        Assert.Equal(PixelFormat.Format32bppArgb, sheet.PixelFormat);

        var opaque = 0;
        for (int y = 0; y < sheet.Height; y += 7)
        {
            for (int x = 0; x < sheet.Width; x += 7)
            {
                var pixel = sheet.GetPixel(x, y);
                Assert.Equal(255, pixel.R);
                Assert.Equal(255, pixel.G);
                Assert.Equal(255, pixel.B);
                if (pixel.A > 0) opaque++;
            }
        }

        Assert.True(opaque > 0, "nothing was drawn");
    }

    /// <summary>
    /// Nothing runs off the edge. The sheet that shipped had three motifs
    /// cut in half by its own border, which is only invisible because the
    /// texture is faint.
    /// </summary>
    [Fact]
    public void NoMotifIsCutOffByTheSheetEdge()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));

        for (int seed = 0; seed < 25; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, library.Motifs);
            using var sheet = SheetRenderer.Render(layout, library);

            for (int x = 0; x < sheet.Width; x++)
            {
                Assert.Equal(0, sheet.GetPixel(x, 0).A);
                Assert.Equal(0, sheet.GetPixel(x, sheet.Height - 1).A);
            }

            for (int y = 0; y < sheet.Height; y++)
            {
                Assert.Equal(0, sheet.GetPixel(0, y).A);
                Assert.Equal(0, sheet.GetPixel(sheet.Width - 1, y).A);
            }
        }
    }

    private static byte[] RenderToPng(int seed)
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));
        var layout = SheetLayout.Build(seed, SheetPixels, GridCells, library.Motifs);
        using var sheet = SheetRenderer.Render(layout, library);

        using var stream = new MemoryStream();
        sheet.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
