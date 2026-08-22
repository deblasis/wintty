using System;
using System.Collections.Generic;
using System.IO;


namespace Ghostty.Core.Settings;

/// <summary>
/// One entry of the bundled shader gallery, deserialized from
/// Assets/Shaders/shaders.json. The manifest is the single source of truth:
/// the verify pipeline (tools/gallery/verify.sh in the repo) compiles and
/// renders every entry it lists, and this catalog shows the same entries
/// in the settings UI.
/// </summary>
public sealed record ShaderGalleryEntry(
    string Id,
    string File,
    string Name,
    string Description,
    string Category,
    string Author,
    string License,
    string Source);

/// <summary>
/// Loads the bundled shader gallery from the app's installed assets. Lives in
/// Core so the test project can drive it against the source tree (a test bin
/// does not receive the app's Content items). The directory resolves the same
/// way in every deployment mode: AppContext.BaseDirectory is the package/exe
/// directory and Content items land in Assets\Shaders next to it.
/// </summary>
public static class ShaderGallery
{
    private static readonly Lazy<IReadOnlyList<ShaderGalleryEntry>> _entries = new(Load);

    public static IReadOnlyList<ShaderGalleryEntry> Entries => _entries.Value;

    /// <summary>
    /// Human-readable detail about how the last load went (why the gallery is
    /// empty, when it is). Set by Load; callers surface it through their own
    /// logger. Null after a successful non-empty load.
    /// </summary>
    public static string? LoadDetail { get; private set; }

    /// <summary>
    /// Tests inject a checkout-relative base here so the loader runs against
    /// the source Assets/Shaders. Null in production: AppContext.BaseDirectory
    /// is used. Must be set before the first Entries read.
    /// </summary>
    public static string? TestBaseDirectory { get; set; }

    /// <summary>
    /// Parses the shaders.json text into entries. Set by the app (source-
    /// generated, NativeAOT-safe) and by tests (reflection, JIT). Must be
    /// set before the first Entries read.
    /// </summary>
    public static Func<string, List<ShaderGalleryEntry>?>? ManifestParser { get; set; }



    private static IReadOnlyList<ShaderGalleryEntry> Load()
    {
        try
        {
            var baseDir = TestBaseDirectory ?? AppContext.BaseDirectory;
            var dir = Path.Combine(baseDir, "Assets", "Shaders");
            var manifestPath = Path.Combine(dir, "shaders.json");
            if (!File.Exists(manifestPath))
            {
                LoadDetail = $"manifest not found at {manifestPath}";
                return Array.Empty<ShaderGalleryEntry>();
            }

            var json = File.ReadAllText(manifestPath);

            // The app publishes NativeAOT (reflection serialization
            // disabled), and the STJ source generator does not fire in this
            // project -- so the manifest binding is INJECTED by the app
            // (ShaderGalleryJson, source-generated context) and by tests
            // (reflection is fine under JIT). A missing parser is a wiring
            // bug and surfaces through LoadDetail.
            var parser = ManifestParser;
            if (parser is null)
            {
                LoadDetail = "no manifest parser wired";
                return Array.Empty<ShaderGalleryEntry>();
            }
            var entries = parser(json) ?? new List<ShaderGalleryEntry>();
            var parsedCount = entries.Count;
            // Keep only entries whose file actually shipped; a stale manifest
            // entry must not offer a shader the renderer cannot load.
            entries.RemoveAll(e =>
                string.IsNullOrWhiteSpace(e.File) ||
                !File.Exists(Path.Combine(dir, e.File)));
            if (entries.Count == 0)
            {
                LoadDetail = $"manifest at {manifestPath} yielded no entries " +
                             $"(parsed {parsedCount}, kept 0 -- files missing at {dir})";
            }
            else
            {
                LoadDetail = null;
            }
            return entries.AsReadOnly();
        }
        catch (Exception ex)
        {
            // A missing or malformed manifest degrades to "no bundled shaders"
            // (the custom-path UI still works), never to a settings crash.
            LoadDetail = $"load failed: {ex.GetType().Name}: {ex.Message}";
            return Array.Empty<ShaderGalleryEntry>();
        }
    }

    /// <summary>
    /// Absolute installed path of a gallery shader file (the value written to
    /// the custom-shader config key when the user picks it).
    /// </summary>
    public static string AbsolutePathFor(ShaderGalleryEntry entry) =>
        Path.Combine(TestBaseDirectory ?? AppContext.BaseDirectory, "Assets", "Shaders", entry.File);
}
