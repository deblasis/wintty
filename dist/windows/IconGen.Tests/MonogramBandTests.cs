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
    /// this instead of silently shipping a band across its feet.
    ///
    /// Scoped to 32 px and up. At 16 and 20 the proportional band is 2 and
    /// 3 px and <see cref="EditionBrand.MinBandPx"/> deliberately floors it
    /// at 4, which costs the ghost's last row of anti-aliasing at those two
    /// sizes. That is a decision, not a regression, so it is stated here
    /// rather than left for this test to trip over.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(160)]
    [InlineData(256)]
    public void BandNeverReachesTheGhost(int size)
    {
        using var masters = MasterRasters.Load(RepoRoot);
        var neutral = EditionBrand.For(Edition.None);
        using var reference = PngWriter.Resize(masters, size, neutral, nightly: false);
        using var icon = PngWriter.Resize(
            masters, size, EditionBrand.For(Edition.Pro), nightly: false);

        int ghostBottom = LowestLitRow(reference);
        int bandTop = TopmostChangedRow(reference, icon);

        Assert.True(
            bandTop > ghostBottom,
            $"At {size} px the band starts at y={bandTop} but the ghost's lowest lit row "
            + $"is y={ghostBottom}, so the band would cover the bottom of the mark.");
    }

    /// <summary>
    /// The flagship carries no furniture. Carrying a mark is what means
    /// "this is an edition"; if this ever draws, that signal is gone.
    /// </summary>
    [Fact]
    public void FlagshipIsLeftAlone()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var plain = masters.Get(256);
        using var icon = masters.Get(256);

        BottomBand.Apply(icon, EditionBrand.For(Edition.None), nightly: false);

        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                Assert.Equal(plain.GetPixel(x, y).ToArgb(), icon.GetPixel(x, y).ToArgb());
    }

    /// <summary>
    /// The band is the edition's own colour, at every rung.
    ///
    /// This is the test the previous design did not have, and it is why
    /// that design shipped a defect for its whole life: the fill was
    /// sampled off the artwork at (50%, 62%), which lands inside the
    /// paperclip's dark metal. Every edition came out the same grey - 96,96,98
    /// for Pro and 96,96,97 for Legacy - so the band distinguished editions
    /// from the flagship and nothing else.
    ///
    /// Sampled at the band's vertical middle and horizontal centre, clear
    /// of the corner arcs and below the top rule.
    /// </summary>
    // Edition is internal, so the ordinal crosses the xUnit boundary and is
    // cast back here rather than making the enum public for the tests.
    [Theory]
    [InlineData((int)Edition.Pro, 160)]
    [InlineData((int)Edition.Enterprise, 160)]
    [InlineData((int)Edition.Legacy, 160)]
    [InlineData((int)Edition.Pro, 40)]
    [InlineData((int)Edition.Legacy, 40)]
    public void BandCarriesTheEditionColour(int editionOrdinal, int size)
    {
        var edition = (Edition)editionOrdinal;
        using var masters = MasterRasters.Load(RepoRoot);
        var brand = EditionBrand.For(edition);
        using var icon = PngWriter.Resize(masters, size, brand, nightly: false);

        int bandHeight = Math.Max(
            EditionBrand.MinBandPx,
            (int)Math.Round(size * EditionBrand.BandHeightFraction));
        // 12 percent in: clear of the corner arc, and clear of the letters,
        // which are held inside the middle 70 percent. Sampling at the centre
        // reads glyph ink on the rungs that have letters.
        int y = size - bandHeight / 2;
        var sampled = icon.GetPixel((int)(size * 0.12), Math.Min(y, size - 1));

        Assert.Equal(brand.BandFill.R, sampled.R);
        Assert.Equal(brand.BandFill.G, sampled.G);
        Assert.Equal(brand.BandFill.B, sampled.B);
    }

    /// <summary>
    /// Letters appear only where they can be read, and the floor is in
    /// OUTPUT pixels.
    ///
    /// The previous floor was expressed in band pixels and evaluated on the
    /// master, so every rung that downsampled from a larger master escaped
    /// it: 40, 48 and 60 px all shipped the smudge it existed to prevent.
    /// The 40 and 48 cases below are that bug, and they fail against the
    /// old code.
    /// </summary>
    [Theory]
    [InlineData(20, false)]
    [InlineData(32, false)]
    [InlineData(40, false)]
    [InlineData(48, false)]
    [InlineData(60, false)]
    [InlineData(64, true)]
    [InlineData(160, true)]
    public void LettersAppearOnlyWhereTheyCanBeRead(int size, bool expectLetters)
    {
        using var masters = MasterRasters.Load(RepoRoot);
        var brand = EditionBrand.For(Edition.Pro);
        using var icon = PngWriter.Resize(masters, size, brand, nightly: false);

        int bandHeight = Math.Max(
            EditionBrand.MinBandPx,
            (int)Math.Round(size * EditionBrand.BandHeightFraction));
        int bandTop = size - bandHeight;

        // Skip the band's own top rule: it is a black wash over the fill,
        // so it satisfies any loose "is this dark?" predicate and would be
        // counted as glyph ink.
        int rule = Math.Max(1, (int)Math.Round(size / 256.0 * 2));

        // Measured against the FILL, not against pure ink. At 64 px the
        // glyph strokes are about one pixel wide and grid-fit anti-aliasing
        // never lays down a fully inked pixel, so a "close to BandInk"
        // probe reports zero on letters that are plainly there. What makes
        // them letters is being markedly darker than the band they sit on.
        double fill = Luminance(brand.BandFill);
        int ink = 0;
        for (int y = bandTop + rule; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = icon.GetPixel(x, y);
                if (c.A <= 200) continue;
                if (Luminance(c) <= fill * 0.6) ink++;
            }

        if (expectLetters)
            Assert.True(ink > 20, $"Expected monogram ink at {size} px; found {ink}.");
        else
            Assert.True(ink == 0,
                $"Expected no monogram ink at {size} px, which is below the "
                + $"{EditionBrand.MinLetterSizePx} px floor; found {ink}.");
    }

    /// <summary>
    /// A nightly build of an edition carries both marks.
    ///
    /// Previously the stripe simply covered the monogram, which was
    /// survivable only because the edition also had a hue. With the hue
    /// gone that would leave a nightly Pro indistinguishable from a nightly
    /// flagship, so the two now split the band.
    /// </summary>
    [Fact]
    public void NightlyEditionKeepsBothMarks()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        var brand = EditionBrand.For(Edition.Pro);
        using var icon = PngWriter.Resize(masters, 160, brand, nightly: true);

        int bandHeight = (int)Math.Round(160 * EditionBrand.BandHeightFraction);
        int bandTop = 160 - bandHeight;

        bool yellow = false, editionFill = false;
        for (int y = bandTop; y < 160; y++)
            for (int x = 0; x < 160; x++)
            {
                var c = icon.GetPixel(x, y);
                if (c.A <= 200) continue;
                if (Near(c, HazardStripe.StripeYellow)) yellow = true;
                if (Near(c, brand.BandFill)) editionFill = true;
            }

        Assert.True(yellow, "nightly stripe missing from a nightly edition icon");
        Assert.True(editionFill, "edition band missing from a nightly edition icon");
    }

    /// <summary>
    /// Every edition's letters stay readable on its own band. Cheap, and it
    /// pins the palette against a future colour tweak that looks fine at
    /// 400 px and disappears at 64.
    /// </summary>
    [Theory]
    [InlineData((int)Edition.Pro)]
    [InlineData((int)Edition.Enterprise)]
    [InlineData((int)Edition.Legacy)]
    [InlineData((int)Edition.Oss)]
    public void BandContrastIsLegible(int editionOrdinal)
    {
        var edition = (Edition)editionOrdinal;
        var brand = EditionBrand.For(edition);
        double ratio = ContrastRatio(brand.BandFill, brand.BandInk);
        Assert.True(ratio >= 4.5,
            $"{edition}'s band is {ratio:F1}:1, below the 4.5:1 floor for small text.");
    }

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static bool Near(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) <= 24;

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static int TopmostChangedRow(Bitmap before, Bitmap after)
    {
        for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
                if (before.GetPixel(x, y).ToArgb() != after.GetPixel(x, y).ToArgb())
                    return y;

        throw new InvalidOperationException(
            "The band changed nothing, so there is no band to place.");
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with images/icons not found");
    }
}
