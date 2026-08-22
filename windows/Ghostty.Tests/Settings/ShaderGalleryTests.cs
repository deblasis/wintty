using System.IO;
using System.Linq;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

// The gallery loader reads Assets/Shaders/shaders.json from the app's
// installed output. A test bin does not receive the app's Content items, so
// these tests point the loader at the source tree instead.
public class ShaderGalleryTests
{
    private static string RepoBase()
    {
        // Test bin lives at windows/Ghostty.Tests/bin/<cfg>/<tfm>; the repo
        // root is five levels up from there.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "windows", "Ghostty", "Assets", "Shaders")))
                return Path.Combine(dir.FullName, "windows", "Ghostty");
            dir = dir.Parent;
        }
        return string.Empty;
    }

    [Fact]
    public void ManifestDeserializesAndKeepsAllEntries()
    {
        var repo = RepoBase();
        if (repo.Length == 0)
        {
            // Not running from a checkout layout (e.g. CI artifact); nothing
            // to assert against.
            return;
        }

        ShaderGallery.TestBaseDirectory = repo;
        try
        {
            var entries = ShaderGallery.Entries;
            Assert.True(entries.Count >= 11,
                $"expected at least 11 bundled shaders, got {entries.Count}; " +
                $"load detail: {ShaderGallery.LoadDetail ?? "(none)"}");
            Assert.All(entries, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Name));
                Assert.False(string.IsNullOrWhiteSpace(e.File));
                Assert.True(File.Exists(ShaderGallery.AbsolutePathFor(e)),
                    $"gallery file missing: {e.File}");
            });
        }
        finally
        {
            ShaderGallery.TestBaseDirectory = null;
        }
    }
}
