using Xunit;

namespace Ghostty.SplashGen.Tests;

public class SheetLayoutTests
{
    /// <summary>
    /// How many seeds every property here is checked over.
    /// </summary>
    /// <remarks>
    /// A layout is one draw from a distribution, so a single seed says
    /// almost nothing: the sheet that started this would have passed on
    /// plenty of individual crops. The interesting failures are the ones
    /// that need an unlucky arrangement to show up, and a sweep is the only
    /// thing that finds them.
    /// </remarks>
    private const int Seeds = 500;

    private const int SheetPixels = 2048;
    private const int GridCells = 5;

    /// <summary>
    /// The committed tiles, read once. Loaded rather than made up so that
    /// adding or removing a motif is covered by these tests instead of
    /// quietly outside them.
    /// </summary>
    private static readonly IReadOnlyList<Motif> Motifs = LoadMotifs();

    private static IReadOnlyList<Motif> LoadMotifs()
    {
        using var library = MotifLibrary.Load(MotifLibrary.DefaultDirectory(RepoRoot.Find()));
        return library.Motifs.ToArray();
    }

    /// <summary>
    /// The property the generator exists for: no motif family appears
    /// within the minimum separation of itself, corners included.
    /// </summary>
    /// <remarks>
    /// <para>Stated on families rather than on tiles because that is the
    /// complaint. Two different staves side by side still read as one
    /// stave printed twice; the eye is not comparing noteheads at the
    /// contrast this is drawn at.</para>
    ///
    /// <para>Every violation across the sweep is collected before the
    /// assertion instead of stopping at the first, so a rule that broke
    /// down at high density reports as the pattern it is rather than as one
    /// unlucky seed.</para>
    /// </remarks>
    [Fact]
    public void NoFamilyEverSitsBesideItself()
    {
        var violations = new List<string>();

        for (int seed = 0; seed < Seeds; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
            violations.AddRange(Violations(layout, seed));
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} same-family neighbours over {Seeds} seeds: "
            + string.Join("; ", violations.Take(10)));
    }

    /// <summary>
    /// Proof that the sweep above can fail.
    /// </summary>
    /// <remarks>
    /// A spacing test passes trivially on an empty sheet, and it would pass
    /// on a sheet whose density had quietly collapsed. Placing the same
    /// motifs on the same cells without the rule has to trip the same
    /// check, or the check is measuring nothing.
    /// </remarks>
    [Fact]
    public void TheCheckCatchesAnUnconstrainedLayout()
    {
        var violations = new List<string>();

        for (int seed = 0; seed < Seeds; seed++)
        {
            var constrained = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
            violations.AddRange(Violations(Unconstrain(constrained, seed), seed));
        }

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Same seed, same layout. Everything else here rests on this.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7331)]
    [InlineData(int.MaxValue)]
    public void SameSeedGivesTheSameLayout(int seed)
    {
        var first = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
        var second = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);

        Assert.Equal(first.Placements, second.Placements);
    }

    /// <summary>
    /// Different seeds, different sheets. Without this, determinism could
    /// be satisfied by a generator that ignores its seed.
    /// </summary>
    [Fact]
    public void DifferentSeedsGiveDifferentLayouts()
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 50; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
            distinct.Add(string.Join(
                "|",
                layout.Placements.Select(p => $"{p.Column},{p.Row},{p.MotifIndex}")));
        }

        Assert.Equal(50, distinct.Count);
    }

    /// <summary>
    /// The density the crop floor was set against.
    /// </summary>
    /// <remarks>
    /// <see cref="Ghostty.Core.Shell.LaunchTexture.MinimumCrop"/> exists
    /// because about half the sheet is empty, and it stops working from
    /// either end: too empty and a crop lands in the gaps, too full and the
    /// motifs stop reading as a scatter. Checked on every sheet in the
    /// sweep rather than on the mean, because it is the worst sheet that
    /// ships badly, not the average one. The sheet this replaced sat at
    /// fourteen cells of twenty five.
    /// </remarks>
    [Fact]
    public void EverySheetIsAboutHalfFull()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            var occupancy = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs).Occupancy;
            Assert.InRange(occupancy, 0.48, 0.60);
        }
    }

    /// <summary>
    /// No block of cells a minimum crop could fall entirely inside is left
    /// empty.
    /// </summary>
    /// <remarks>
    /// The structural half of what
    /// <see cref="Ghostty.Core.Shell.LaunchTexture.MinimumCrop"/> needs.
    /// Density says how much is on the sheet; this says it is not all in
    /// one place. Asserted on the layout because it can be checked over
    /// five hundred seeds here for the cost of one render.
    /// </remarks>
    [Fact]
    public void NoBlockACropCouldFallIntoIsEmpty()
    {
        var block = SheetLayout.CropBlockCells(GridCells);

        for (int seed = 0; seed < Seeds; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
            var empty = EmptyBlocks(layout.Placements, GridCells, block);

            Assert.True(
                empty.Count == 0,
                $"seed {seed}: {block}x{block} blocks with nothing in them at "
                + string.Join(" ", empty));
        }
    }

    /// <summary>
    /// Proof that the block check can fail. Scattering the same number of
    /// motifs over the same grid without caring where leaves holes, which
    /// is what the sheet that shipped did and what
    /// <see cref="SheetLayout.CropBlockCells"/> exists to rule out.
    /// </summary>
    [Fact]
    public void TheBlockCheckCatchesAnUnspreadLayout()
    {
        var block = SheetLayout.CropBlockCells(GridCells);
        var holes = 0;

        for (int seed = 0; seed < Seeds; seed++)
        {
            var random = new Random(seed);
            var count = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs).Placements.Count;
            var scattered = Enumerable.Range(0, GridCells * GridCells)
                .OrderBy(_ => random.Next())
                .Take(count)
                .Select(i => new Placement(i % GridCells, i / GridCells, 0, 0, 0, 0))
                .ToArray();

            holes += EmptyBlocks(scattered, GridCells, block).Count;
        }

        Assert.True(holes > 0, "an unspread layout never left a hole, so the check proves nothing");
    }

    /// <summary>
    /// No family is crowded out. The hard rule turns placements away, and
    /// a family that is always the one turned away would vanish from the
    /// sheet without any of the other tests noticing.
    /// </summary>
    [Fact]
    public void EveryFamilyKeepsAShareOfTheSheet()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var motif in Motifs) counts[motif.Family] = 0;

        var placed = 0;
        for (int seed = 0; seed < Seeds; seed++)
        {
            foreach (var placement in SheetLayout.Build(seed, SheetPixels, GridCells, Motifs).Placements)
            {
                counts[Motifs[placement.MotifIndex].Family]++;
                placed++;
            }
        }

        var floor = placed / (counts.Count * 3.0);
        foreach (var (family, count) in counts)
            Assert.True(count > floor, $"{family} took only {count} of {placed} placements");
    }

    /// <summary>
    /// A motif stays in the cell it was placed in. The spacing rule counts
    /// cells, so a jitter wide enough to walk a motif into its neighbour
    /// would satisfy the rule and break the thing the rule is for.
    /// </summary>
    [Fact]
    public void MotifsStayInTheCellTheyWerePlacedIn()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
            var cell = layout.CellPixels;

            foreach (var placement in layout.Placements)
            {
                Assert.InRange(placement.CentreX, placement.Column * cell, (placement.Column + 1) * cell);
                Assert.InRange(placement.CentreY, placement.Row * cell, (placement.Row + 1) * cell);
                Assert.InRange(
                    placement.TurnDegrees,
                    -SheetLayout.MaximumTurnDegrees,
                    SheetLayout.MaximumTurnDegrees);
            }
        }
    }

    /// <summary>
    /// Rotation is a draw, not a decoration. A generator that turned every
    /// motif by the same amount would pass the range check above.
    /// </summary>
    [Fact]
    public void RotationVariesBetweenPlacements()
    {
        var layout = SheetLayout.Build(4242, SheetPixels, GridCells, Motifs);
        var angles = layout.Placements.Select(p => p.TurnDegrees).ToArray();

        Assert.Equal(angles.Length, angles.Distinct().Count());
        Assert.Contains(angles, a => a < 0);
        Assert.Contains(angles, a => a > 0);
    }

    /// <summary>
    /// One cell can never hold two motifs, whatever the shuffle does.
    /// </summary>
    [Fact]
    public void EachCellIsUsedAtMostOnce()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            var layout = SheetLayout.Build(seed, SheetPixels, GridCells, Motifs);
            var cells = layout.Placements.Select(p => (p.Column, p.Row)).ToArray();
            Assert.Equal(cells.Length, cells.Distinct().Count());
        }
    }

    private static List<string> EmptyBlocks(
        IReadOnlyList<Placement> placements, int gridCells, int block)
    {
        var taken = new bool[gridCells, gridCells];
        foreach (var placement in placements) taken[placement.Column, placement.Row] = true;

        var empty = new List<string>();
        for (int row = 0; row + block <= gridCells; row++)
        {
            for (int column = 0; column + block <= gridCells; column++)
            {
                var found = false;
                for (int y = row; y < row + block && !found; y++)
                    for (int x = column; x < column + block && !found; x++)
                        found = taken[x, y];

                if (!found) empty.Add($"({column},{row})");
            }
        }

        return empty;
    }

    private static IEnumerable<string> Violations(Layout layout, int seed)
    {
        var placements = layout.Placements;
        for (int i = 0; i < placements.Count; i++)
        {
            for (int j = i + 1; j < placements.Count; j++)
            {
                var left = placements[i];
                var right = placements[j];
                if (Motifs[left.MotifIndex].Family != Motifs[right.MotifIndex].Family) continue;

                var distance = Math.Max(
                    Math.Abs(left.Column - right.Column),
                    Math.Abs(left.Row - right.Row));
                if (distance >= SheetLayout.MinimumSeparationCells) continue;

                yield return $"seed {seed}: {Motifs[left.MotifIndex].Family} at "
                    + $"({left.Column},{left.Row}) and ({right.Column},{right.Row})";
            }
        }
    }

    /// <summary>
    /// The same cells and the same motif count, chosen without the rule.
    /// </summary>
    private static Layout Unconstrain(Layout layout, int seed)
    {
        var random = new Random(seed);
        var placements = layout.Placements
            .Select(p => p with { MotifIndex = random.Next(Motifs.Count) })
            .ToArray();
        return layout with { Placements = placements };
    }
}
