using Xunit;

namespace Ghostty.IconGen.Tests;

// #1096, the routing half. PngWriterTests pins the resolver and its
// once-per-process cache, but nothing there forces a Save call site to
// go through them: reverting WriteLadder or IcoWriter to
// Image.Save(path, ImageFormat.Png) -- the exact #1096 failure, a codec
// lookup per save -- wrote identical files on a healthy GDI+ and left
// every test green. The regression is visible only in the source, so
// this scans it, the way the Ghostty.Tests wiring guards scan theirs.
//
// The rule: in this tool's sources, "ImageFormat.Png" may appear only
// as "ImageFormat.Png.Guid", the resolver's format-identity comparison.
// Any bare occurrence is an ImageFormat argument back in a Save call.
public class SourceScanTests
{
    [Fact]
    public void NoIconGenSourceSavesThroughTheImageFormatOverload()
    {
        var sources = Directory.GetFiles(
            Path.Combine(TempDir.FindRepoRoot(), "dist", "windows", "IconGen"),
            "*.cs");

        // An empty corpus would mean the scan silently stopped looking,
        // which for a guard is the worst failure mode there is.
        Assert.NotEmpty(sources);
        foreach (var path in sources)
        {
            var source = File.ReadAllText(path);
            var bareUse = source
                .Replace("ImageFormat.Png.Guid", string.Empty)
                .Contains("ImageFormat.Png");

            Assert.False(bareUse,
                $"{Path.GetFileName(path)} uses ImageFormat.Png outside "
                + "ImageFormat.Png.Guid. Every PNG save in IconGen must go "
                + "through PngWriter.PngEncoder, the explicit encoder "
                + "resolved once per process (#1096); an ImageFormat "
                + "argument is the per-call codec lookup that came back "
                + "encoder-less under signoff load.");
        }
    }
}
