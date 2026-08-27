namespace Ghostty.SplashGen;

/// <summary>Where one motif goes, and how far it is turned.</summary>
/// <param name="Column">Grid column it was placed in.</param>
/// <param name="Row">Grid row it was placed in.</param>
/// <param name="MotifIndex">Index into the motif list the layout was built from.</param>
/// <param name="CentreX">Centre of the motif, in sheet pixels.</param>
/// <param name="CentreY">Centre of the motif, in sheet pixels.</param>
/// <param name="TurnDegrees">Rotation about that centre.</param>
public readonly record struct Placement(
    int Column,
    int Row,
    int MotifIndex,
    double CentreX,
    double CentreY,
    double TurnDegrees);

/// <summary>What the generator decided, before anything is drawn.</summary>
public sealed record Layout(
    int SheetPixels,
    int GridCells,
    IReadOnlyList<Placement> Placements)
{
    public double CellPixels => (double)SheetPixels / GridCells;

    public double Occupancy => (double)Placements.Count / (GridCells * GridCells);
}

/// <summary>
/// Chooses which cells of the sheet carry a motif, which motif each one
/// carries, and how far it is turned.
/// </summary>
/// <remarks>
/// <para>This is the part the hand-authored sheet had no way to state. The
/// sheet it replaced put the same motif beside itself three times over,
/// which reads as a tile repeating rather than as a scatter, and nothing
/// could catch that because the layout only existed as pixels. Here it is a
/// list of placements, so the property can be asserted.</para>
///
/// <para>The rule has two halves and they do different jobs.</para>
///
/// <para>The hard half: a family may not appear within
/// <see cref="MinimumSeparationCells"/> cells of itself, measured as a
/// king's move so that diagonals count. That is the property worth
/// promising, and a soft penalty cannot promise it -- a penalty only makes
/// a bad placement unlikely, and over enough sheets unlikely happens. What
/// makes the hard half safe to state is the escape: a cell with no
/// admissible motif is simply left empty. There is no backtracking and no
/// failure case, which is what a hard rule usually costs.</para>
///
/// <para>The soft half: among the motifs that are admissible, weight each
/// one down by how crowded its family already is nearby, falling off with
/// the square of the distance, and weight a repeat of the exact same tile
/// down harder still. This is what stops the layout from merely clearing
/// the bar -- without it the sheet fills with same-family pairs sitting at
/// exactly the minimum separation, which is legal and still looks
/// patterned. One pass, no state beyond what has already been placed.</para>
///
/// <para>Considered and rejected: wave function collapse and a constraint
/// solver. Both exist to satisfy constraints that interlock, where a choice
/// here forbids a choice four cells away and the only way through is
/// propagation and backtracking. Nothing here interlocks. Each cell's
/// admissible set is decided entirely by what is already on the board, and
/// a cell that cannot be filled costs one empty cell rather than a dead
/// end, so the machinery would buy nothing over a single weighted pass.
/// Also rejected: Poisson-disc sampling, which spaces points apart without
/// caring what they are, and the complaint is about what they are.</para>
/// </remarks>
public static class SheetLayout
{
    /// <summary>
    /// How far apart two motifs of the same family must be, in cells,
    /// measured as a king's move.
    /// </summary>
    /// <remarks>
    /// Two is "never touching", including corner to corner. One would
    /// permit exactly the arrangement that started this. Three is not
    /// reachable: with three families on a grid this size it would empty
    /// out more cells than the crop floor can afford.
    /// </remarks>
    public const int MinimumSeparationCells = 2;

    /// <summary>
    /// How many cells carry a motif, as a fraction.
    /// </summary>
    /// <remarks>
    /// About half, which is where the sheet that shipped sat -- fourteen
    /// cells of twenty five -- and what
    /// <see cref="Ghostty.Core.Shell.LaunchTexture.MinimumCrop"/> was set
    /// against. It is a target and not a promise: the spacing rule can
    /// refuse the last few cells, so a sheet lands at or a little under
    /// this.
    /// </remarks>
    public const double TargetOccupancy = 0.54;

    /// <summary>How far a motif may be turned, in degrees, either way.</summary>
    /// <remarks>
    /// <para>Enough that two instances of one family do not look stamped
    /// from the same die, and enough to break the horizon a grid of upright
    /// motifs would otherwise draw across the sheet.</para>
    ///
    /// <para>Deliberately small. The crop selector already turns the whole
    /// sheet by up to fifty-five degrees and stops there for a stated
    /// reason: writing that stands near vertical stops reading as writing
    /// and becomes a row of marks. Per-motif rotation composes with that
    /// one, so a wide range here would push some motifs past the point the
    /// selector is careful to stay inside.</para>
    /// </remarks>
    public const double MaximumTurnDegrees = 15.0;

    /// <summary>
    /// How far the centre of a motif may wander from the centre of its
    /// cell, as a fraction of the cell.
    /// </summary>
    /// <remarks>
    /// Enough to stop the placements from lining up into visible rows and
    /// columns, and bounded so that this plus
    /// <see cref="MotifReachCells"/> stays well under a whole cell. The
    /// spacing rule counts cells, so a motif that wandered far enough to
    /// reach past its immediate neighbour would satisfy the rule and break
    /// what the rule is for at the same time.
    /// </remarks>
    public const double CentreJitterCells = 0.14;

    /// <summary>
    /// How far the soft penalty reaches, in cells.
    /// </summary>
    /// <remarks>
    /// Past the hard separation, since inside it the weight is already
    /// zero. This is the band where a placement is allowed but discouraged.
    /// </remarks>
    public const double CrowdingRadiusCells = 3.5;

    /// <summary>How much crowding costs. Larger spreads families harder.</summary>
    public const double CrowdingStrength = 8.0;

    /// <summary>
    /// How far a motif can reach from its centre once it is turned, as a
    /// fraction of a cell.
    /// </summary>
    /// <remarks>
    /// <para>Placements near the border are pulled in by this much so that
    /// nothing is cut in half by the edge of the sheet. The sheet this
    /// replaced had three motifs running off its own border, which only
    /// went unnoticed because the texture is drawn so faint; a crop taken
    /// against an edge shows the cut.</para>
    ///
    /// <para>A bound rather than a measurement, so the layout stays free of
    /// the tiles' dimensions and can be reasoned about without them. The
    /// worst case is a square motif filling
    /// <see cref="SheetRenderer.MotifFillFraction"/> of the cell and turned
    /// the full <see cref="MaximumTurnDegrees"/>, which reaches
    /// fill * (cos + sin) / 2 of a cell. The tiles that ship are wider than
    /// they are tall and land under that.</para>
    /// </remarks>
    public const double MotifReachCells = 0.53;

    /// <summary>
    /// What one nearby instance of the exact same tile costs, relative to a
    /// nearby instance of a different tile from the same family.
    /// </summary>
    /// <remarks>
    /// Two staves near each other is a repeat; the same stave near itself
    /// is a copy, and reads worse. The families are small -- two or three
    /// tiles each -- so this has to be a preference rather than a rule or
    /// the tile choice would be forced.
    /// </remarks>
    public const double RepeatedTileWeight = 3.0;

    /// <summary>
    /// Lay out one sheet.
    /// </summary>
    /// <param name="seed">
    /// The whole of the variation. The same seed and the same motif list
    /// always give the same layout, which is what makes the spacing rule
    /// testable and the shipped asset reproducible.
    /// </param>
    /// <param name="sheetPixels">Edge of the square sheet, in pixels.</param>
    /// <param name="gridCells">Cells along one edge.</param>
    /// <param name="motifs">
    /// What may be placed. Order matters: placements name motifs by index.
    /// </param>
    public static Layout Build(
        int seed, int sheetPixels, int gridCells, IReadOnlyList<Motif> motifs)
    {
        ArgumentNullException.ThrowIfNull(motifs);
        ArgumentOutOfRangeException.ThrowIfLessThan(sheetPixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(gridCells, 1);
        if (motifs.Count == 0)
            throw new ArgumentException("No motifs to place", nameof(motifs));

        var random = new Random(seed);

        var cells = new (int Column, int Row)[gridCells * gridCells];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = (i % gridCells, i / gridCells);
        Shuffle(random, cells);

        var state = new Board(motifs, sheetPixels, gridCells);

        // The one thing that has to be true comes first. The spacing rule
        // refuses a cell whose neighbourhood already holds every family,
        // and a half-filled board produces those, so a pass that runs
        // afterwards to plug the gaps finds the gaps sealed shut: on a five
        // by five it failed to place anything on one seed in forty and left
        // exactly the blank window it was there to prevent. Run first, on an
        // open board, it always finds room.
        CoverEveryBlockACropCanFallInto(random, state, cells);

        // The rest is scatter, taken in the shuffled order, and stops at the
        // wanted density rather than after a fixed number of tries, so cells
        // the spacing rule turns down cost nothing.
        var target = (int)Math.Round(TargetOccupancy * cells.Length);
        foreach (var cell in cells)
        {
            if (state.Placements.Count >= target) break;
            state.TryPlace(random, cell.Column, cell.Row);
        }

        return new Layout(sheetPixels, gridCells, state.Placements);
    }

    /// <summary>
    /// Put a motif in every square block of cells that a crop could
    /// otherwise fall entirely inside.
    /// </summary>
    /// <remarks>
    /// <para>Half the cells being empty is the look; a hole is not. A
    /// random half of a five by five grid leaves a two by two block empty
    /// about a third of the time, and a crop taken at the floor spans two
    /// and a bit cells, so it fits inside that hole and comes up blank.
    /// That is the failure <see cref="Ghostty.Core.Shell.LaunchTexture.MinimumCrop"/>
    /// exists to prevent, and choosing cells uniformly does not prevent it.
    /// Measured rather than supposed: without this pass the crop probe
    /// finds a blank window on a third of seeds.</para>
    ///
    /// <para>Which cell of a block gets the motif is a draw, so this
    /// spreads the sheet out without putting a regular lattice under it.
    /// The blocks overlap heavily -- sixteen of them on a five by five --
    /// so most are already covered by the time they are reached and only a
    /// handful of placements come from here.</para>
    /// </remarks>
    private static void CoverEveryBlockACropCanFallInto(
        Random random, Board state, (int Column, int Row)[] shuffledCells)
    {
        var block = CropBlockCells(state.GridCells);
        var last = state.GridCells - block;

        for (int row = 0; row <= last; row++)
        {
            for (int column = 0; column <= last; column++)
            {
                if (state.BlockHasMotif(column, row, block)) continue;

                // Walked in the shuffled order so that which cell of the
                // block gets the motif is a draw like everything else,
                // rather than always its top left corner.
                foreach (var cell in shuffledCells)
                {
                    if (cell.Column < column || cell.Column >= column + block) continue;
                    if (cell.Row < row || cell.Row >= row + block) continue;
                    if (state.TryPlace(random, cell.Column, cell.Row)) break;
                }
            }
        }
    }

    /// <summary>
    /// The largest square block of whole cells a minimum crop is guaranteed
    /// to contain, whatever its position.
    /// </summary>
    /// <remarks>
    /// A window spanning L cells along an axis, landing anywhere, always
    /// covers at least ceil(L) - 1 of them completely. At the shipped five
    /// by five that is two, so a two by two block with a motif somewhere in
    /// it is enough to promise every crop something to show.
    /// </remarks>
    internal static int CropBlockCells(int gridCells)
        => Math.Max(
            1,
            (int)Math.Ceiling(Ghostty.Core.Shell.LaunchTexture.MinimumCrop * gridCells) - 1);

    /// <summary>
    /// What is on the sheet so far, and the one operation that adds to it.
    /// </summary>
    private sealed class Board
    {
        private readonly IReadOnlyList<Motif> _motifs;
        private readonly List<Placement> _placements = [];
        private readonly bool[,] _taken;
        private readonly double[] _weights;
        private readonly double[] _shares;
        private readonly double _cellPixels;
        private readonly int _sheetPixels;

        public Board(IReadOnlyList<Motif> motifs, int sheetPixels, int gridCells)
        {
            _motifs = motifs;
            _sheetPixels = sheetPixels;
            GridCells = gridCells;
            _cellPixels = (double)sheetPixels / gridCells;
            _taken = new bool[gridCells, gridCells];
            _weights = new double[motifs.Count];
            _shares = FamilyShares(motifs);
        }

        public int GridCells { get; }

        public IReadOnlyList<Placement> Placements => _placements;

        public bool BlockHasMotif(int column, int row, int size)
        {
            for (int y = row; y < row + size; y++)
                for (int x = column; x < column + size; x++)
                    if (_taken[x, y]) return true;
            return false;
        }

        public bool TryPlace(Random random, int column, int row)
        {
            if (_taken[column, row]) return false;

            double total = 0;
            for (int m = 0; m < _motifs.Count; m++)
            {
                _weights[m] = Weight(_motifs, _placements, m, column, row, _shares[m]);
                total += _weights[m];
            }

            // Every family is already too close to this cell. Leaving it
            // empty is the whole reason the hard rule can be stated as one:
            // the alternative is backtracking, and an empty cell costs
            // nothing the sheet cannot afford.
            if (total <= 0) return false;

            var chosen = Sample(random, _weights, total);

            var reach = MotifReachCells * _cellPixels;
            var centreX = KeepOnSheet(
                ((column + 0.5) + Jitter(random)) * _cellPixels, reach, _sheetPixels);
            var centreY = KeepOnSheet(
                ((row + 0.5) + Jitter(random)) * _cellPixels, reach, _sheetPixels);
            var turn = ((random.NextDouble() * 2.0) - 1.0) * MaximumTurnDegrees;

            _taken[column, row] = true;
            _placements.Add(new Placement(column, row, chosen, centreX, centreY, turn));
            return true;
        }
    }

    /// <summary>
    /// How willing the layout is to put this motif in this cell. Zero means
    /// the spacing rule forbids it.
    /// </summary>
    private static double Weight(
        IReadOnlyList<Motif> motifs,
        IReadOnlyList<Placement> placed,
        int candidate,
        int column,
        int row,
        double baseWeight)
    {
        var family = motifs[candidate].Family;

        double crowding = 0;
        foreach (var placement in placed)
        {
            if (!string.Equals(motifs[placement.MotifIndex].Family, family, StringComparison.Ordinal))
                continue;

            var dx = placement.Column - column;
            var dy = placement.Row - row;

            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < MinimumSeparationCells)
                return 0;

            var distanceSquared = (double)((dx * dx) + (dy * dy));
            if (distanceSquared > CrowdingRadiusCells * CrowdingRadiusCells) continue;

            crowding += (placement.MotifIndex == candidate ? RepeatedTileWeight : 1.0)
                / distanceSquared;
        }

        return baseWeight / (1.0 + (CrowdingStrength * crowding));
    }

    /// <summary>
    /// How much of a family's share each of its tiles starts with.
    /// </summary>
    /// <remarks>
    /// Families get equal footing, not tiles. Drawing tiles uniformly makes
    /// a family's share of the sheet depend on how many variants happened
    /// to be cut for it: three formula tiles against two staves and two
    /// matrix columns put formulas on nearly half the sheet, which reads as
    /// a page of text with a few pictures on it rather than as a mix.
    /// </remarks>
    private static double[] FamilyShares(IReadOnlyList<Motif> motifs)
    {
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var motif in motifs)
            sizes[motif.Family] = sizes.GetValueOrDefault(motif.Family) + 1;

        var shares = new double[motifs.Count];
        for (int i = 0; i < motifs.Count; i++)
            shares[i] = 1.0 / sizes[motifs[i].Family];
        return shares;
    }

    /// <summary>Draw one motif from the weighted distribution.</summary>
    private static int Sample(Random random, double[] weights, double total)
    {
        var target = random.NextDouble() * total;
        double running = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            running += weights[i];
            if (target < running) return i;
        }

        // Floating point can leave the running total a hair under the
        // target on the last step. The last non-zero weight is the answer
        // in that case, and never a zero-weight one: returning a forbidden
        // motif here is exactly the bug this whole file exists to stop.
        for (int i = weights.Length - 1; i >= 0; i--)
            if (weights[i] > 0) return i;

        throw new InvalidOperationException("Sampled from an all-zero distribution");
    }

    private static double Jitter(Random random)
        => ((random.NextDouble() * 2.0) - 1.0) * CentreJitterCells;

    /// <summary>
    /// Pull a centre far enough from the border that a turned motif still
    /// fits. A sheet only one cell across cannot hold one, so it is centred
    /// and the overhang is accepted rather than clamped to an empty range.
    /// </summary>
    private static double KeepOnSheet(double centre, double reach, int sheetPixels)
    {
        var low = reach;
        var high = sheetPixels - reach;
        return low > high ? sheetPixels / 2.0 : Math.Clamp(centre, low, high);
    }

    private static void Shuffle(Random random, (int Column, int Row)[] cells)
    {
        for (int i = cells.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (cells[i], cells[j]) = (cells[j], cells[i]);
        }
    }
}
