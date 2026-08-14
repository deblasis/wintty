using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

public class LaunchIconAssetsTests
{
    // Ghostty.csproj, embedded by Ghostty.Tests.csproj. Read as text: this
    // project deliberately does not reference Ghostty.csproj, which would
    // drag the WinAppSDK MRT/PRI targets into a plain net10.0 assembly.
    private const string ProjectResourceName = "Ghostty.Tests.Shell.Ghostty.csproj";

    // Spelled out rather than built from LaunchIconRung.FileName: this is
    // the check that a rename of that pattern leaves nothing behind in the
    // project file, so it has to know the old shape independently.
    private static readonly Regex SplashAssetPattern = new(
        @"SplashIcon\.scale-\d+\.png",
        RegexOptions.Compiled);

    [Fact]
    public void RungsAreTheLadderWeShip()
    {
        var rungs = LaunchIconAssets.Rungs.Select(r => (r.Scale, r.Pixels)).ToArray();

        Assert.Equal(new[] { (100, 160), (150, 240), (200, 320), (400, 640) }, rungs);
    }

    [Fact]
    public void RungsAscendByPixelSize()
    {
        // SplashWindow.IconPathForSize takes the first rung at least as
        // large as the size it needs and treats the last as the largest
        // shipped. Out of order, it would pick a rung it has to upscale
        // and fall back to one that is not the biggest.
        var pixels = LaunchIconAssets.Rungs.Select(r => r.Pixels).ToArray();

        Assert.Equal(pixels.OrderBy(p => p).ToArray(), pixels);
    }

    [Fact]
    public void FileNamesFollowTheWinuiScaleSuffix()
    {
        foreach (var rung in LaunchIconAssets.Rungs)
        {
            Assert.Equal($"SplashIcon.scale-{rung.Scale}.png", rung.FileName);
        }
    }

    // The csproj holds the one copy of the ladder that cannot read the
    // shared table: <Content Include> is evaluated by MSBuild, and it has
    // to be an explicit list because a wildcard expands before the icons
    // are generated. A rung listed there but not shipped is an MSB3030
    // build failure; a rung shipped but not listed never reaches
    // bin\Assets, and the splash then paints a bare rectangle.
    [Fact]
    public void ProjectFileCopiesEverySharedRung()
    {
        var project = ReadEmbeddedProject();

        foreach (var rung in LaunchIconAssets.Rungs)
        {
            Assert.True(
                project.Contains(rung.FileName, StringComparison.Ordinal),
                $"Ghostty.csproj does not copy {rung.FileName}. Add it to the " +
                "SplashIcon <Content Include> list and to GenerateBrandingAssets' " +
                "Outputs.");
        }
    }

    // The other direction: a rung dropped or renumbered leaves the copy
    // item pointing at a file IconGen no longer writes, which fails the
    // build with MSB3030 rather than skipping quietly.
    [Fact]
    public void ProjectFileCopiesNothingButTheSharedRungs()
    {
        var project = ReadEmbeddedProject();
        var shipped = LaunchIconAssets.Rungs.Select(r => r.FileName).ToHashSet(StringComparer.Ordinal);

        var stale = SplashAssetPattern.Matches(project)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !shipped.Contains(name))
            .ToArray();

        Assert.True(
            stale.Length == 0,
            "Ghostty.csproj names launch icons that are not shipped rungs: "
                + string.Join(", ", stale));
    }

    private static string ReadEmbeddedProject()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ProjectResourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
