using System.Drawing;
using Xunit;

namespace Ghostty.IconGen.Tests;

public class MonogramBandTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// The constraint the band exists under: it must sit below the mark,
    /// not on top of it.
    ///
    /// Measured against the shipping artwork rather than asserted against
    /// the constant, so that redrawing the icon with a longer ghost fails
    /// this instead of silently shipping a band across its feet. The
    /// failure message carries both numbers because the fix is either a
    /// smaller band or a redrawn master, and which one depends on how far
    /// apart they are.
    /// </summary>
    [Fact]
    public void BandNeverReachesTheGhost()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        int ghostBottom = LowestLitRow(icon);
        int bandTop = 256 - (int)Math.Round(256 * EditionBrand.BandHeightFraction);

        Assert.True(
            bandTop > ghostBottom,
            $"The monogram band starts at y={bandTop} but the ghost's lowest lit row "
            + $"is y={ghostBottom}, so the band would cover the bottom of the mark. "
            + $"Either lower EditionBrand.BandHeightFraction (currently "
            + $"{EditionBrand.BandHeightFraction}) or raise the ghost in the master.");
    }

    /// <summary>
    /// The plate is a rounded square and the band runs to the bottom
    /// edge, so a plain rectangle fill squares off the bottom corners.
    /// The corner pixel is transparent in the master and has to stay
    /// transparent afterwards.
    /// </summary>
    [Fact]
    public void BandKeepsThePlatesRoundedCorners()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        var cornerBefore = icon.GetPixel(1, 254);
        Assert.True(cornerBefore.A < 40,
            $"Test assumes the master's bottom-left corner is transparent; got alpha "
            + $"{cornerBefore.A}. If the plate is no longer rounded this test is moot.");

        MonogramBand.Apply(icon, EditionBrand.For(Edition.Pro));

        var cornerAfter = icon.GetPixel(1, 254);
        Assert.True(cornerAfter.A < 40,
            $"The band squared off the plate's rounded corner: alpha went from "
            + $"{cornerBefore.A} to {cornerAfter.A}.");
    }

    [Fact]
    public void BandCoversTheBottomBandAndNothingAbove()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        var before = Snapshot(icon);
        MonogramBand.Apply(icon, EditionBrand.For(Edition.Pro));

        int bandTop = 256 - (int)Math.Round(256 * EditionBrand.BandHeightFraction);

        for (int y = 0; y < bandTop; y++)
            for (int x = 0; x < 256; x++)
                Assert.Equal(before[x, y], icon.GetPixel(x, y).ToArgb());

        int changed = 0;
        for (int y = bandTop; y < 256; y++)
            for (int x = 0; x < 256; x++)
                if (before[x, y] != icon.GetPixel(x, y).ToArgb()) changed++;

        Assert.True(changed > 1000, $"Expected the band to repaint its rows; {changed} pixels changed.");
    }

    /// <summary>
    /// The flagship carries no monogram, so a mark on an icon means "this
    /// is an edition". If this ever draws, every icon gains furniture and
    /// the signal is gone.
    /// </summary>
    [Fact]
    public void FlagshipIsLeftAlone()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        var before = Snapshot(icon);
        MonogramBand.Apply(icon, EditionBrand.For(Edition.None));

        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                Assert.Equal(before[x, y], icon.GetPixel(x, y).ToArgb());
    }

    /// <summary>
    /// At 16 and 32 px the band is two to five pixels tall. Letters there
    /// are a smudge that reads as a rendering fault, so the band is drawn
    /// plain and those sizes lean entirely on the hue, as intended. By
    /// 256 px the letters are the whole point.
    ///
    /// Asserted on the presence of letter ink rather than on a colour
    /// count: the plate's curved edge yields many partial-alpha variants
    /// of the one band colour, so counting distinct values measures
    /// anti-aliasing, not glyphs.
    /// </summary>
    [Theory]
    [InlineData(16, false)]
    [InlineData(32, false)]
    [InlineData(256, true)]
    public void LettersAppearOnlyWhereTheyCanBeRead(int size, bool expectLetters)
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(size);

        MonogramBand.Apply(icon, EditionBrand.For(Edition.Pro));

        int bandHeight = Math.Max(2, (int)Math.Round(size * EditionBrand.BandHeightFraction));
        int bandTop = size - bandHeight;

        int ink = 0;
        for (int y = bandTop; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = icon.GetPixel(x, y);
                if (c.A > 200 && c.R < 60 && c.G < 60 && c.B < 70) ink++;
            }

        if (expectLetters)
            Assert.True(ink > 40, $"Expected monogram ink at {size} px; found {ink} dark pixels.");
        else
            Assert.True(ink == 0,
                $"Expected no monogram ink at {size} px (band is {bandHeight} px tall, "
                + $"below the {EditionBrand.MinLegibleBandPx} px legibility floor); found {ink}.");
    }

    private static int LowestLitRow(Bitmap icon)
    {
        // The ghost is the only near-white, opaque thing on the icon; the
        // bezel is a mid silver and the screen is saturated.
        for (int y = icon.Height - 1; y >= 0; y--)
        {
            int lit = 0;
            for (int x = 0; x < icon.Width; x++)
            {
                var c = icon.GetPixel(x, y);
                if (c.A > 128 && c.R > 200 && c.G > 200 && c.B > 200) lit++;
            }
            if (lit >= 3) return y;
        }
        throw new InvalidOperationException("No lit ghost rows found in the master.");
    }

    private static int[,] Snapshot(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width, bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                pixels[x, y] = bitmap.GetPixel(x, y).ToArgb();
        return pixels;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with images/icons not found");
    }
}
