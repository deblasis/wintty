using Xunit;

namespace Ghostty.SplashGen.Tests;

public class MotifLibraryTests
{
    [Theory]
    [InlineData("stave-a", "stave")]
    [InlineData("matrix-b", "matrix")]
    [InlineData("maths-c", "maths")]
    [InlineData("plain", "plain")]
    [InlineData("-leading", "-leading")]
    public void FamilyIsTheNameUpToTheFirstHyphen(string id, string expected)
        => Assert.Equal(expected, MotifLibrary.FamilyOf(id));

    /// <summary>
    /// The hard spacing rule needs somewhere to go. With one family every
    /// cell after the first would be refused and the sheet would come out
    /// nearly empty; with two, half of them would. Three is the floor at
    /// this grid and this density, and the tiles that ship give exactly
    /// three, so removing a family is a decision that should fail here
    /// rather than show up as a thin sheet.
    /// </summary>
    [Fact]
    public void TheCommittedTilesCoverAtLeastThreeFamilies()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));

        var families = library.Motifs.Select(m => m.Family).Distinct().ToArray();
        Assert.True(families.Length >= 3, $"only {string.Join(", ", families)}");
    }

    /// <summary>
    /// The tiles are masks, like the sheet they build. A tile carrying
    /// colour would survive rendering as colour, and a tile whose
    /// transparent pixels are not white would bleed grey into its own
    /// edges the moment it is scaled or turned.
    /// </summary>
    [Fact]
    public void EveryTileIsWhiteWithTheMarkInAlpha()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));

        for (int i = 0; i < library.Motifs.Count; i++)
        {
            var image = library.Image(i);
            for (int y = 0; y < image.Height; y += 3)
            {
                for (int x = 0; x < image.Width; x += 3)
                {
                    var pixel = image.GetPixel(x, y);
                    Assert.True(
                        pixel.R == 255 && pixel.G == 255 && pixel.B == 255,
                        $"{library.Motifs[i].Id} at ({x},{y}) is {pixel}");
                }
            }
        }
    }

    /// <summary>
    /// The bound the layout keeps motifs off the sheet border by has to
    /// actually bound the tiles. It is stated as a constant so the layout
    /// need not know their dimensions, which means nothing else would
    /// notice a tile shaped differently enough to break it.
    /// </summary>
    [Fact]
    public void NoTileReachesFurtherThanTheLayoutAssumes()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));

        var radians = SheetLayout.MaximumTurnDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        for (int i = 0; i < library.Motifs.Count; i++)
        {
            var image = library.Image(i);
            var scale = SheetRenderer.MotifFillFraction / Math.Max(image.Width, image.Height);
            var reach = Math.Max(
                ((image.Width * cos) + (image.Height * sin)) * scale / 2.0,
                ((image.Width * sin) + (image.Height * cos)) * scale / 2.0);

            Assert.True(
                reach <= SheetLayout.MotifReachCells,
                $"{library.Motifs[i].Id} reaches {reach:F3} cells, "
                + $"over the {SheetLayout.MotifReachCells} the layout allows");
        }
    }

    /// <summary>
    /// A jittered motif never reaches past the cells next to its own. The
    /// spacing rule is stated in cells, and it only means anything while
    /// that is true.
    /// </summary>
    [Fact]
    public void JitterAndReachTogetherStayInsideOneCellOfTheirOwn()
        => Assert.True(
            SheetLayout.CentreJitterCells + SheetLayout.MotifReachCells < 1.0,
            "a motif can be placed far enough to cross a whole cell");
}
