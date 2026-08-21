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

            // Edition first, channel second. The nightly stripe and the
            // edition band want the same bottom band, and a nightly build
            // of an edition needs to read as nightly before it reads as
            // that edition - a dev build shipped as if it were stable is
            // the more expensive confusion. One consequence worth knowing:
            // on a nightly edition build the stripe covers the monogram, so
            // those icons carry the hue cue alone.
            //
            // The unmarked flagship skips branding entirely rather than
            // running two no-op transforms over a clone. That is not just
            // saved work: it keeps the default path byte-identical to what
            // this tool produced before editions existed, which Cli.cs
            // states as a requirement and which an extra Bitmap round-trip
            // would quietly put at risk.
            var brand = EditionBrand.For(options.Edition);
            using var branded = IsNeutral(brand) ? null : BrandMasters(loaded, brand);
            var masters = branded ?? loaded;

            if (options.Channel == Channel.Nightly)
            {
                using var striped = StripeMasters(masters);
                PngWriter.WriteScalePngs(striped, options.OutputDir);
                IcoWriter.Write(striped, Path.Combine(options.OutputDir, "wintty.ico"));
            }
            else
            {
                PngWriter.WriteScalePngs(masters, options.OutputDir);
                IcoWriter.Write(masters, Path.Combine(options.OutputDir, "wintty.ico"));
            }

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
                    Path.Combine(options.OutputDir, "wintty-settings.ico"));
            }

            using (var inspectorMasters = GlyphMasters.Render("\uEBE8"))
            {
                IcoWriter.Write(
                    inspectorMasters,
                    Path.Combine(options.OutputDir, "wintty-inspector.ico"));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IconGen failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Whether this brand changes nothing, so the masters can be used as
    /// loaded. Asks the brand rather than the <see cref="Edition"/> so a
    /// future edition that happens to be neutral takes the same path.
    /// </summary>
    private static bool IsNeutral(EditionBrand brand)
        => brand.HueShiftDegrees == 0
           && brand.SaturationScale == 1.0
           && string.IsNullOrEmpty(brand.Monogram);

    private static MasterRasters BrandMasters(MasterRasters original, EditionBrand brand)
    {
        // Caller disposes the returned instance.
        var dict = new Dictionary<int, System.Drawing.Bitmap>();
        foreach (var px in original.Sizes)
        {
            var bitmap = original.Get(px); // MasterRasters.Get clones
            TierTint.Apply(bitmap, brand);
            MonogramBand.Apply(bitmap, brand);
            dict[px] = bitmap;
        }
        return MasterRasters.FromDictionary(dict);
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
