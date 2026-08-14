using System;
using System.Collections.Generic;
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

    // SplashWindow.cs, from the interop source corpus embedded for
    // MarshalComplianceTests. Located by suffix because the resource name
    // carries the folder path with whatever separator the build host used.
    private const string SplashWindowResourceSuffix = "SplashWindow.cs";

    // Spelled out rather than built from LaunchIconRung.FileName: this is
    // the check that a rename of that pattern leaves nothing behind in the
    // project file, so it has to know the old shape independently.
    private const string SplashAssetPattern = @"SplashIcon\.scale-\d+\.png";

    // The two places the project file names generated icons: the copy list
    // that puts them in bin\Assets, and the up-to-date check that decides
    // whether they get generated at all. Both have to agree with the shared
    // ladder, and each has to be read on its own -- a scan of the whole file
    // is satisfied by either one, which is how a missing copy item hides.
    private static readonly Regex ContentIncludeAttribute = new(
        @"<Content\s+Include=""(?<items>[^""]*)""",
        RegexOptions.Compiled);
    private static readonly Regex OutputsAttribute = new(
        @"Outputs=""(?<items>[^""]*)""",
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

    // The copy list is the one that decides whether the PNGs reach
    // bin\Assets. A rung missing from it is generated and then left behind,
    // and the splash paints a bare rectangle at runtime.
    [Fact]
    public void ProjectFileCopiesExactlyTheSharedRungs()
    {
        AssertNamesExactlyTheSharedRungs(
            SplashAssetsIn(ContentIncludeAttribute),
            "the SplashIcon <Content Include> list");
    }

    // The up-to-date check is the one that decides whether they are
    // generated. A rung missing from it lets MSBuild skip the target with
    // the file absent, which the unconditional copy item turns into an
    // MSB3030 build failure.
    [Fact]
    public void GenerationOutputsNameExactlyTheSharedRungs()
    {
        AssertNamesExactlyTheSharedRungs(
            SplashAssetsIn(OutputsAttribute),
            "GenerateBrandingAssets' Outputs");
    }

    // The AppIcon ladder has no shared table to compare against (nothing in
    // C# resolves those files by name), but its two lists still have to
    // agree with each other for the same two reasons as above.
    [Fact]
    public void AppIconCopyListAndGenerationOutputsAgree()
    {
        var copied = AssetsIn(ContentIncludeAttribute, @"AppIcon\.scale-\d+\.png");
        var generated = AssetsIn(OutputsAttribute, @"AppIcon\.scale-\d+\.png");

        Assert.NotEmpty(copied);
        Assert.Equal(
            generated.OrderBy(n => n, StringComparer.Ordinal),
            copied.OrderBy(n => n, StringComparer.Ordinal));
    }

    // Nothing else can pin this: SplashWindow lives in Ghostty.csproj, which
    // this project does not reference. Without the check, reverting
    // IconPathForSize to its own hardcoded rung table leaves every test
    // above green while the consumer drifts away from the shared one again.
    [Fact]
    public void SplashWindowResolvesIconsThroughTheSharedLadder()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("Ghostty.Tests.Interop.Sources.", StringComparison.Ordinal)
                && n.EndsWith(SplashWindowResourceSuffix, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            names.Length == 1,
            $"Expected exactly one embedded {SplashWindowResourceSuffix}, found "
                + $"[{string.Join(", ", names)}].");

        var source = ReadEmbeddedText(names[0]);

        Assert.Contains("LaunchIconAssets.Rungs", source, StringComparison.Ordinal);
        // The prefix alone, not the full file name: a revert that rebuilds
        // the name with the same interpolation the shared rung uses would
        // slip past a pattern that insists on the digits and extension.
        Assert.DoesNotContain("SplashIcon.scale-", source, StringComparison.Ordinal);
    }

    private static void AssertNamesExactlyTheSharedRungs(
        IReadOnlyCollection<string> named, string what)
    {
        var shipped = LaunchIconAssets.Rungs
            .Select(r => r.FileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            shipped.SequenceEqual(named.OrderBy(n => n, StringComparer.Ordinal)),
            $"Ghostty.csproj: {what} names [{string.Join(", ", named)}], but the "
                + $"shared ladder ships [{string.Join(", ", shipped)}].");
    }

    private static IReadOnlyCollection<string> SplashAssetsIn(Regex attribute)
        => AssetsIn(attribute, SplashAssetPattern);

    /// <summary>
    /// File names matching <paramref name="assetPattern"/> that appear
    /// inside an <paramref name="attribute"/> value, so that a mention in a
    /// comment or in the other list cannot stand in for the real entry.
    /// </summary>
    private static IReadOnlyCollection<string> AssetsIn(Regex attribute, string assetPattern)
    {
        var project = ReadEmbeddedText(ProjectResourceName);
        var asset = new Regex(assetPattern);

        return attribute.Matches(project)
            .SelectMany(m => asset.Matches(m.Groups["items"].Value).Select(a => a.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
