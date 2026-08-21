using System.Drawing;
using Xunit;

namespace Ghostty.IconGen.Tests;

public class TierTintTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// The whole reason the tint is gated on saturation: the ghost is
    /// the brand, and it has to survive every edition unchanged. If this
    /// fails, the five editions stop looking like one product.
    /// </summary>
    [Fact]
    public void TheGhostAndTheBezelKeepTheirColour()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        var neutrals = new List<(int X, int Y, Color Before)>();
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                var c = icon.GetPixel(x, y);
                if (c.A > 200 && IsNeutral(c)) neutrals.Add((x, y, c));
            }

        Assert.True(neutrals.Count > 500,
            $"Expected the master to contain neutral ghost/bezel pixels; found {neutrals.Count}.");

        TierTint.Apply(icon, EditionBrand.For(Edition.Pro));

        foreach (var (x, y, before) in neutrals)
        {
            var after = icon.GetPixel(x, y);
            Assert.Equal(before.ToArgb(), after.ToArgb());
        }
    }

    [Fact]
    public void TheScreenActuallyMoves()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        // A point inside the screen, above the band and below the ghost's
        // head, chosen so it is screen rather than mark.
        var before = icon.GetPixel(30, 128);
        TierTint.Apply(icon, EditionBrand.For(Edition.Pro));
        var after = icon.GetPixel(30, 128);

        Assert.NotEqual(before.ToArgb(), after.ToArgb());
    }

    [Fact]
    public void EditionsLandOnDistinctHues()
    {
        var editions = new[] { Edition.Pro, Edition.Enterprise, Edition.Legacy, Edition.Oss };
        var seen = new List<(Edition Edition, Color Colour)>();

        using var masters = MasterRasters.Load(RepoRoot);
        foreach (var edition in editions)
        {
            using var icon = masters.Get(256);
            TierTint.Apply(icon, EditionBrand.For(edition));
            seen.Add((edition, icon.GetPixel(30, 128)));
        }

        for (int i = 0; i < seen.Count; i++)
            for (int j = i + 1; j < seen.Count; j++)
            {
                int distance = Math.Abs(seen[i].Colour.R - seen[j].Colour.R)
                    + Math.Abs(seen[i].Colour.G - seen[j].Colour.G)
                    + Math.Abs(seen[i].Colour.B - seen[j].Colour.B);
                Assert.True(distance > 60,
                    $"{seen[i].Edition} and {seen[j].Edition} render the screen too close "
                    + $"together (channel distance {distance}); they will not be tellable "
                    + "apart in a taskbar.");
            }
    }

    [Fact]
    public void FlagshipIsLeftAlone()
    {
        using var masters = MasterRasters.Load(RepoRoot);
        using var icon = masters.Get(256);

        var before = icon.GetPixel(30, 128);
        TierTint.Apply(icon, EditionBrand.For(Edition.None));

        Assert.Equal(before.ToArgb(), icon.GetPixel(30, 128).ToArgb());
    }

    private static bool IsNeutral(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max == 0) return true;
        // Mirrors TierTint's cutoff closely enough to select the same
        // pixels without reaching into its internals.
        double lightness = (max + min) / 510.0;
        double delta = (max - min) / 255.0;
        if (delta == 0) return true;
        double saturation = lightness > 0.5
            ? delta / (2.0 - (max + min) / 255.0)
            : delta / ((max + min) / 255.0);
        return saturation < 0.12;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with images/icons not found");
    }
}
