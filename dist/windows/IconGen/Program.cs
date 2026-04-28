namespace Ghostty.IconGen;

internal static class Program
{
    public static int Main(string[] args)
    {
        // When invoked from MSBuild, CWD is the IconGen project directory.
        // Walk up to find the repo root (directory containing images/icons).
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        return Run(args, repoRoot);
    }

    public static int Run(string[] args, string repoRoot)
    {
        try
        {
            var options = Cli.Parse(args);
            Directory.CreateDirectory(options.OutputDir);

            using var masters = MasterRasters.Load(repoRoot);

            if (options.Channel == Channel.Nightly)
            {
                using var striped = StripeMasters(masters);
                PngWriter.WriteScalePngs(striped, options.OutputDir);
                IcoWriter.Write(striped, Path.Combine(options.OutputDir, "ghostty.ico"));
            }
            else
            {
                PngWriter.WriteScalePngs(masters, options.OutputDir);
                IcoWriter.Write(masters, Path.Combine(options.OutputDir, "ghostty.ico"));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IconGen failed: {ex.Message}");
            return 1;
        }
    }

    private static MasterRasters StripeMasters(MasterRasters original)
    {
        // Caller disposes the returned instance.
        var dict = new Dictionary<int, System.Drawing.Bitmap>();
        foreach (var px in original.Sizes)
        {
            var bitmap = original.Get(px); // MasterRasters.Get clones
            HazardStripe.Apply(bitmap);
            dict[px] = bitmap;
        }
        return MasterRasters.FromDictionary(dict);
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
