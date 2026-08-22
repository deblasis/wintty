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

    /// <summary>
    /// Tests inject a checkout-relative base here so the loader runs against
    /// the source Assets/Shaders (a test bin does not receive the app's
    /// Content items). Null in production: AppContext.BaseDirectory is used.
    /// Must be set before the first Entries read.
    /// </summary>
    internal static string? TestBaseDirectory { get; set; }

    private sealed class Manifest
    {
        public List<ShaderGalleryEntry>? Shaders { get; set; }
    }

    private static IReadOnlyList<ShaderGalleryEntry> Load()
    {
        try
        {
            var baseDir = TestBaseDirectory ?? AppContext.BaseDirectory;
            var dir = Path.Combine(baseDir, "Assets", "Shaders");
            var manifestPath = Path.Combine(dir, "shaders.json");
            if (!File.Exists(manifestPath))
            {
                Logging.StaticLoggers.SettingsConfigWriter.LogInformation(
                    "shader gallery manifest not found at {Path}", manifestPath);
                return Array.Empty<ShaderGalleryEntry>();
            }

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
            if (entries.Count == 0)
            {
                Logging.StaticLoggers.SettingsConfigWriter.LogInformation(
                    "shader gallery is empty after loading {Path} (raw entries: {Raw})",
                    manifestPath,
                    manifest?.Shaders?.Count ?? -1);
            }
            return entries.AsReadOnly();
        }
        catch (Exception ex)
        {
            // A missing or malformed manifest degrades to "no bundled shaders"
            // (the custom-path UI still works), never to a settings crash.
            Logging.StaticLoggers.SettingsConfigWriter.LogInformation(
                "shader gallery load failed: {Type}: {Message}",
                ex.GetType().Name, ex.Message);
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
