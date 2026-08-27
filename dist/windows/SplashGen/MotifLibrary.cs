using System.Drawing;

namespace Ghostty.SplashGen;

/// <summary>
/// One motif the sheet can be built from: what it is, and what it counts as
/// for the purpose of not standing next to itself.
/// </summary>
/// <param name="Id">The tile's file name without its extension.</param>
/// <param name="Family">
/// What a reader would call it -- a stave, a formula, a column of matrix
/// characters. The spacing rule is enforced on this rather than on
/// <paramref name="Id"/>, because two different staves side by side still
/// read as the same thing twice, which is the complaint.
/// </param>
public readonly record struct Motif(string Id, string Family);

/// <summary>
/// The committed motif tiles, and the bitmaps behind them.
/// </summary>
/// <remarks>
/// <para>The tiles were cut out of the sheet that shipped, one clean
/// instance of each distinct motif, rather than redrawn. Everything on that
/// sheet is glyphs and straight lines -- a grand staff is a font's clef and
/// notehead code points over five ruled lines, the formulas are a line of
/// text each, the matrix columns are katakana and digits down a fading
/// gradient -- so a renderer could have reproduced all of it. Cutting was
/// still the better trade: it is the same ink, exactly, which is what lets
/// a regenerated sheet be compared against the old one on layout alone, and
/// it leaves the output free of any dependence on which fonts happen to be
/// installed. A generator that draws different letterforms on a different
/// machine cannot promise the same seed gives the same sheet.</para>
///
/// <para>The tiles are white with the mark carried in alpha, matching what
/// the splash expects of the sheet: it tints a mask at draw time, so any
/// colour in here would be thrown away. Transparent pixels are white too,
/// so resampling a rotated tile cannot pull grey out of nowhere along its
/// edges.</para>
///
/// <para>Provenance, in sheet pixels of the 2048x2048 asset at the commit
/// that introduced it, each trimmed to its ink and padded by two:
/// stave-a (16,24), stave-b (32,1304), maths-a (824,64), maths-b (872,472),
/// maths-c (360,1728), matrix-a (848,1240), matrix-b (904,1704). The three
/// instances the old sheet ran off its own edges were skipped, and the
/// duplicates -- the same two staves appear seven times between them -- were
/// taken once.</para>
/// </remarks>
public sealed class MotifLibrary : IDisposable
{
    private readonly Bitmap[] _images;

    private MotifLibrary(Motif[] motifs, Bitmap[] images)
    {
        Motifs = motifs;
        _images = images;
    }

    /// <summary>Where the tiles live, relative to the repo root.</summary>
    public static string DefaultDirectory(string repoRoot)
        => Path.Combine(repoRoot, "dist", "windows", "SplashGen", "Motifs");

    public IReadOnlyList<Motif> Motifs { get; }

    /// <summary>The tile behind a motif. Owned here; do not dispose.</summary>
    public Bitmap Image(int index) => _images[index];

    /// <summary>
    /// A tile's family is its name up to the first hyphen, so the family a
    /// motif belongs to is visible in the file listing rather than recorded
    /// in a manifest that can disagree with it.
    /// </summary>
    public static string FamilyOf(string id)
    {
        var hyphen = id.IndexOf('-');
        return hyphen <= 0 ? id : id[..hyphen];
    }

    public static MotifLibrary Load(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Motif directory {directory} not found");

        // Sorted, because the layout addresses motifs by index and an
        // index that depends on the order the filesystem happened to hand
        // them back would make the same seed give a different sheet.
        var paths = Directory.GetFiles(directory, "*.png");
        Array.Sort(paths, StringComparer.Ordinal);

        if (paths.Length == 0)
            throw new InvalidOperationException($"No motif tiles in {directory}");

        var motifs = new Motif[paths.Length];
        var images = new Bitmap[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            var id = Path.GetFileNameWithoutExtension(paths[i]);
            motifs[i] = new Motif(id, FamilyOf(id));
            images[i] = new Bitmap(paths[i]);
        }

        return new MotifLibrary(motifs, images);
    }

    public void Dispose()
    {
        foreach (var image in _images) image.Dispose();
    }
}
