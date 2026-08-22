using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// <summary>The shaders.json shape. Kept internal; only the entries are public.</summary>
internal sealed class ShaderGalleryManifest
{
    [JsonPropertyName("shaders")]
    public List<ShaderGalleryEntry>? Shaders { get; set; }
}

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

    // The app publishes NativeAOT: reflection-based serialization is disabled,
    // so the manifest binds through a source-generated context (the
    // WindowStateContext pattern; the context must be a top-level internal
    // partial class or the source generator emits nothing). JIT test runs
    // would not catch a reflection fallback here -- only the published app
    // fails, with exactly the NotSupportedException the combo surfaces.
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(ShaderGalleryManifest))]
    internal sealed partial class ShaderGalleryContext : JsonSerializerContext;
    

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
            var manifest = JsonSerializer.Deserialize(
                json, ShaderGalleryContext.Default.ShaderGalleryManifest);

            var entries = manifest?.Shaders ?? new List<ShaderGalleryEntry>();
            // Keep only entries whose file actually shipped; a stale manifest
            // entry must not offer a shader the renderer cannot load.
            entries.RemoveAll(e =>
                string.IsNullOrWhiteSpace(e.File) ||
                !File.Exists(Path.Combine(dir, e.File)));
            if (entries.Count == 0)
            {
                LoadDetail = $"manifest at {manifestPath} yielded no entries " +
                             $"(raw: {manifest?.Shaders?.Count.ToString() ?? "null"})";
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
