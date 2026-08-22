using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ghostty.Settings;

/// <summary>
/// One entry of the bundled shader gallery, deserialized from
/// Assets/Shaders/shaders.json. The manifest is the single source of truth:
/// the verify pipeline (tools/gallery/verify.sh) compiles and renders every
/// entry it lists, and this catalog shows the same entries in the UI.
/// </summary>
internal sealed record ShaderGalleryEntry(
    string Id,
    string File,
    string Name,
    string Description,
    string Category,
    string Author,
    string License,
    string Source);

/// <summary>
/// Loads the bundled shader gallery from the app's installed assets. The
/// directory resolves the same way in every deployment mode (framework-
/// dependent, single-file, AOT, MSIX): AppContext.BaseDirectory is the
/// package/exe directory and Content items land in Assets\Shaders next to it.
/// </summary>
internal static class ShaderGallery
{
    private static readonly Lazy<IReadOnlyList<ShaderGalleryEntry>> _entries = new(Load);

    public static IReadOnlyList<ShaderGalleryEntry> Entries => _entries.Value;

    private sealed class Manifest
    {
        public List<ShaderGalleryEntry>? Shaders { get; set; }
    }

    private static IReadOnlyList<ShaderGalleryEntry> Load()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");
            var manifestPath = Path.Combine(dir, "shaders.json");
            if (!File.Exists(manifestPath))
                return Array.Empty<ShaderGalleryEntry>();

            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            var entries = manifest?.Shaders ?? new List<ShaderGalleryEntry>();
            // Keep only entries whose file actually shipped; a stale manifest
            // entry must not offer a shader the renderer cannot load.
            entries.RemoveAll(e =>
                string.IsNullOrWhiteSpace(e.File) ||
                !File.Exists(Path.Combine(dir, e.File)));
            return entries.AsReadOnly();
        }
        catch
        {
            // A missing or malformed manifest degrades to "no bundled shaders"
            // (the custom-path UI still works), never to a settings crash.
            return Array.Empty<ShaderGalleryEntry>();
        }
    }

    /// <summary>
    /// Absolute installed path of a gallery shader file (the value written to
    /// the custom-shader config key when the user picks it).
    /// </summary>
    public static string AbsolutePathFor(ShaderGalleryEntry entry) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", entry.File);
}
