namespace Ghostty.SplashGen;

internal sealed record Options(
    int Seed,
    string OutputPath,
    int SheetPixels,
    int GridCells,
    string? MotifDirectory);

internal static class Cli
{
    /// <summary>Edge of the sheet the splash ships today.</summary>
    public const int DefaultSheetPixels = 2048;

    /// <summary>
    /// Cells along one edge.
    /// </summary>
    /// <remarks>
    /// Five, because that is the grid the hand-authored sheet was laid out
    /// on and the motif tiles were cut at that scale. Changing it rescales
    /// every motif, so it is an argument rather than a constant, but a
    /// different value produces a visibly different sheet and not merely a
    /// rearranged one.
    /// </remarks>
    public const int DefaultGridCells = 5;

    public static Options Parse(string[] args)
    {
        int? seed = null;
        string? outputPath = null;
        var sheetPixels = DefaultSheetPixels;
        var gridCells = DefaultGridCells;
        string? motifDirectory = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--seed requires a value");
                    seed = ParseInt(args[++i], "--seed");
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--out requires a value");
                    outputPath = args[++i];
                    break;
                case "--size":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--size requires a value");
                    sheetPixels = ParseInt(args[++i], "--size");
                    if (sheetPixels < 1)
                        throw new ArgumentException("--size must be positive");
                    break;
                case "--grid":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--grid requires a value");
                    gridCells = ParseInt(args[++i], "--grid");
                    if (gridCells < 1)
                        throw new ArgumentException("--grid must be positive");
                    break;
                case "--motifs":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--motifs requires a value");
                    motifDirectory = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'");
            }
        }

        // Required rather than defaulted. The seed is the sheet's identity:
        // a default would let someone regenerate the shipped asset from a
        // command line that does not say which sheet it produces.
        if (seed is null)
            throw new ArgumentException("--seed is required");
        if (outputPath is null)
            throw new ArgumentException("--out is required");

        return new Options(seed.Value, outputPath, sheetPixels, gridCells, motifDirectory);
    }

    private static int ParseInt(string value, string option)
        => int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{option} expects a whole number, got '{value}'");
}
