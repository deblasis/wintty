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

        // Exact equality is the wrong assertion, and finding out why is the
        // point of testing this by chroma. A pure grey (chroma 0) has HSL
        // saturation 0 and the gate skips it outright. A near-white with a
        // few counts of chroma computes a saturation near 1.0, because the
        // HSL denominator collapses as lightness approaches 1 - so the gate
        // does rotate it, by a hue it barely has. The visible result is
        // nothing; the bytes move.
        //
        // So: pure greys must be untouched, and everything else near-grey
        // must stay visually identical. The bound is the measured worst case
        // plus headroom, not a guess - the largest single-channel move over
        // the whole master is 12/255, on a bezel corner pixel. A regression
        // that actually recoloured the ghost is nowhere near this: rotating
        // a genuinely saturated pixel moves channels by 100 or more.
        int worst = 0;
        (int X, int Y) worstAt = (0, 0);
        foreach (var (x, y, before) in neutrals)
        {
            var after = icon.GetPixel(x, y);
            int shift = Math.Max(Math.Abs(before.R - after.R),
                Math.Max(Math.Abs(before.G - after.G), Math.Abs(before.B - after.B)));
            if (shift > worst) { worst = shift; worstAt = (x, y); }

            if (before.R == before.G && before.G == before.B)
            {
                Assert.True(before.ToArgb() == after.ToArgb(),
                    $"A pure grey at ({x},{y}) moved: {before} -> {after}. The "
                    + "saturation gate must skip these outright.");
            }
        }

        Assert.True(worst <= 16,
            $"A near-neutral pixel moved by {worst}/255 at {worstAt}, which is more "
            + "than the rounding the hue rotation can account for. The ghost and the "
            + "bezel are the brand: if they shift, the five editions stop looking "
            + "like one product.");
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
        // None is in the set on purpose: the flagship is what an edition
        // shortcut sits next to in the Start menu, so "distinct from each
        // other" is only half the requirement.
        var editions = new[]
        {
            Edition.None, Edition.Pro, Edition.Enterprise, Edition.Legacy, Edition.Oss,
        };
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

    /// <summary>
    /// Near-grey by chroma, deliberately NOT by the HSL saturation
    /// <see cref="TierTint"/> gates on.
    ///
    /// The first version of this test recomputed that same saturation with
    /// a tighter cutoff, which made it a strict subset of what the gate
    /// already skips: it could only fail if the gate inverted, never if
    /// the gate's threshold moved or its formula changed. Chroma is an
    /// independent measure, so this now fails when the ghost actually
    /// starts moving.
    ///
    /// It also happens to be the honest measure here. HSL saturation is 1.0
    /// for any pixel whose max channel is 255 and whose lightness is above
    /// 0.5, so a near-white (245,247,255) reads as fully saturated - which
    /// is why "the ghost is low-saturation" was never quite the right
    /// description of why it survives.
    /// </summary>
    private static bool IsNeutral(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max - min <= 12;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with images/icons not found");
    }
}
