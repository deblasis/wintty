using System.Drawing.Imaging;

namespace Ghostty.SplashGen;

internal static class Program
{
    public static int Main(string[] args)
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        return Run(args, repoRoot);
    }

    public static int Run(string[] args, string repoRoot)
    {
        try
        {
            var options = Cli.Parse(args);

            var motifDirectory = options.MotifDirectory
                ?? MotifLibrary.DefaultDirectory(repoRoot);
            using var library = MotifLibrary.Load(motifDirectory);

            var layout = SheetLayout.Build(
                options.Seed, options.SheetPixels, options.GridCells, library.Motifs);
            using var sheet = SheetRenderer.Render(layout, library);

            var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            sheet.Save(options.OutputPath, ImageFormat.Png);

            var coverage = CropCoverage.Probe(sheet);
            Report(options, library, layout, coverage);

            // The sheet is only useful if the narrowest crop the splash can
            // take still has something in it. Failing here rather than
            // printing a warning is deliberate: the output is an asset
            // somebody is about to commit, and a warning on a build log is
            // not where that decision gets made.
            if (coverage.MinimumInkFraction < CropCoverage.MinimumInkFraction)
            {
                Console.Error.WriteLine(
                    $"SplashGen: the emptiest crop holds "
                    + $"{coverage.MinimumInkFraction:P2} ink, under the "
                    + $"{CropCoverage.MinimumInkFraction:P2} the splash needs. "
                    + "Raise the occupancy or lower the grid.");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SplashGen failed: {ex.Message}");
            return 1;
        }
    }

    private static void Report(
        Options options, MotifLibrary library, Layout layout, CoverageReport coverage)
    {
        var families = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var placement in layout.Placements)
        {
            var family = library.Motifs[placement.MotifIndex].Family;
            families[family] = families.GetValueOrDefault(family) + 1;
        }

        Console.WriteLine($"seed {options.Seed}, {options.SheetPixels} px, "
            + $"{options.GridCells}x{options.GridCells} cells");
        Console.WriteLine($"placed {layout.Placements.Count} of "
            + $"{options.GridCells * options.GridCells} cells "
            + $"({layout.Occupancy:P1} occupancy)");

        foreach (var (family, count) in families)
            Console.WriteLine($"  {family}: {count}");

        Console.WriteLine($"ink {coverage.SheetInkFraction:P2} over the sheet; "
            + $"emptiest {coverage.CropPixels}px crop holds "
            + $"{coverage.MinimumInkFraction:P2} at "
            + $"({coverage.WorstX},{coverage.WorstY})");
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "images", "icons")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Repo root with images/icons not found");
    }
}
