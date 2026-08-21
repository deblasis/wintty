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

            using var loaded = MasterRasters.Load(repoRoot);

            // Both marks are applied per output rung by BottomBand, which
            // also decides how a nightly build of an edition divides the
            // band between them. Nothing is painted onto the masters, so
            // the unmarked flagship on the stable channel still walks the
            // same path it did before editions existed: load, downsample,
            // save. Cli.cs states that as a requirement.
            var brand = EditionBrand.For(options.Edition);
            bool nightly = options.Channel == Channel.Nightly;

            PngWriter.WriteScalePngs(loaded, options.OutputDir, brand, nightly);
            IcoWriter.Write(
                loaded, Path.Combine(options.OutputDir, "wintty.ico"), brand, nightly);

            // The Settings and inspector windows get their own glyph .icos so
            // the taskbar group / alt-tab list distinguish them from a terminal
            // window. Each glyph matches that window's in-chrome affordance, so
            // the OS-level slots read the same as the UI:
            //   U+E713 "Settings" (gear) = SettingsWindow.xaml's TitleBar.IconSource
            //   U+EBE8 "Bug"             = the command palette's "Toggle Inspector"
            // Channel-independent because these are UI affordances, not brand
            // marks. Both code points exist identically in Segoe Fluent Icons
            // and Segoe MDL2 Assets.
            using (var settingsMasters = GlyphMasters.Render("\uE713"))
            {
                IcoWriter.Write(
                    settingsMasters,
                    Path.Combine(options.OutputDir, "wintty-settings.ico"),
                    EditionBrand.For(Edition.None), nightly: false);
            }

            using (var inspectorMasters = GlyphMasters.Render("\uEBE8"))
            {
                IcoWriter.Write(
                    inspectorMasters,
                    Path.Combine(options.OutputDir, "wintty-inspector.ico"),
                    EditionBrand.For(Edition.None), nightly: false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IconGen failed: {ex.Message}");
            return 1;
        }
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
