using System.Drawing;
using System.Drawing.Imaging;
using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.SplashGen.Tests;

public class CropCoverageTests
{
    private const int SheetPixels = 2048;
    private const int GridCells = 5;

    /// <summary>
    /// The floor the splash relies on: no window the crop selector can take
    /// comes up blank.
    /// </summary>
    /// <remarks>
    /// <para>This is the reason a density number matters at all. Occupancy
    /// is a proxy; a sheet can sit at exactly the right occupancy and still
    /// have every motif bunched into one corner. The probe measures the
    /// thing itself, over every position rather than a sample of them.</para>
    ///
    /// <para>Ten seeds rather than the five hundred the layout sweep uses,
    /// because each one renders a two megapixel sheet and builds a table
    /// over it. The layout sweep is what covers arrangement; this covers
    /// the arrangement actually reaching the pixels.</para>
    /// </remarks>
    [Fact]
    public void NoMinimumCropComesUpBlank()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));

        for (int seed = 0; seed < 10; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, library.Motifs);
            using var sheet = SheetRenderer.Render(layout, library);

            var report = CropCoverage.Probe(sheet);

            Assert.Equal((int)(LaunchTexture.MinimumCrop * SheetPixels), report.CropPixels);
            Assert.True(
                report.MinimumInkFraction >= CropCoverage.MinimumInkFraction,
                $"seed {seed}: emptiest crop at ({report.WorstX},{report.WorstY}) holds "
                + $"{report.MinimumInkFraction:P3}");
        }
    }

    /// <summary>
    /// Proof the probe can fail: an empty sheet has to come back empty.
    /// A summed-area table with an off-by-one in it would report ink that
    /// is not there, and every other assertion here would still pass.
    /// </summary>
    [Fact]
    public void AnEmptySheetReportsNoInk()
    {
        using var blank = new Bitmap(SheetPixels, SheetPixels, PixelFormat.Format32bppArgb);

        var report = CropCoverage.Probe(blank);

        Assert.Equal(0.0, report.MinimumInkFraction);
        Assert.Equal(0.0, report.SheetInkFraction);
    }

    /// <summary>
    /// And that a fully inked sheet comes back full, which pins the other
    /// end of the same arithmetic.
    /// </summary>
    [Fact]
    public void AFullSheetReportsAllInk()
    {
        using var full = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(full))
            graphics.Clear(Color.White);

        var report = CropCoverage.Probe(full);

        Assert.Equal(1.0, report.MinimumInkFraction, 6);
        Assert.Equal(1.0, report.SheetInkFraction, 6);
    }
}
