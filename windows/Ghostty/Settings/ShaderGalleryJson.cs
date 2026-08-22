using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghostty.Core.Settings;

/// <summary>
/// NativeAOT-safe manifest binding for the shader gallery: the app disables
/// reflection serialization, so the manifest parses through a source-
/// generated context (the WindowStateContext pattern). JIT-only reflection
/// would pass tests and fail in the published app with the
/// NotSupportedException the combo's diagnostic item surfaces.
/// </summary>
internal sealed class ShaderGalleryManifest
{
    [JsonPropertyName("shaders")]
    public List<ShaderGalleryEntry>? Shaders { get; set; }
}

[JsonSerializable(typeof(ShaderGalleryManifest))]
internal partial class ShaderGalleryContext : JsonSerializerContext
{
}

internal static class ShaderGalleryJson
{
    public static List<ShaderGalleryEntry>? Parse(string json) =>
        JsonSerializer.Deserialize(json, ShaderGalleryContext.Default.ShaderGalleryManifest)?.Shaders;
}
