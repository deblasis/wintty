using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghostty.Core.Settings;

namespace Ghostty.Settings;

/// <summary>
/// NativeAOT-safe manifest binding for the shader gallery: the app disables
/// reflection serialization, so the manifest parses through a source-
/// generated context (the WindowStateContext pattern). JIT-only reflection
/// would pass tests and fail in the published app with the
/// NotSupportedException the combo's diagnostic item surfaces.
///
/// The DTO spells every JSON name with [JsonPropertyName] rather than
/// relying on any option: source-generated constructor binding for records
/// matches parameter names CASE-SENSITIVELY, and the manifest keys are
/// lowercase -- positional-record binding left File null and the shipped-
/// file filter dropped every entry ("manifest yielded no entries").
/// </summary>
internal sealed class ShaderGalleryManifestDto
{
    [JsonPropertyName("shaders")]
    public List<ShaderGalleryEntryDto>? Shaders { get; set; }
}

internal sealed class ShaderGalleryEntryDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }

    public ShaderGalleryEntry ToEntry() => new(
        Id ?? string.Empty,
        File ?? string.Empty,
        Name ?? string.Empty,
        Description ?? string.Empty,
        Category ?? string.Empty,
        Author ?? string.Empty,
        License ?? string.Empty,
        Source ?? string.Empty);
}

[JsonSerializable(typeof(ShaderGalleryManifestDto))]
internal partial class ShaderGalleryContext : JsonSerializerContext
{
}

internal static class ShaderGalleryJson
{
    public static List<ShaderGalleryEntry>? Parse(string json) =>
        JsonSerializer.Deserialize(json, ShaderGalleryContext.Default.ShaderGalleryManifestDto)
            ?.Shaders?
            .Select(e => e.ToEntry())
            .ToList();
}
