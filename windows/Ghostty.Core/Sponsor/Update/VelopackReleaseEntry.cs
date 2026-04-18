using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Row in the Velopack manifest JSON (what <c>vpk pack</c> emits and
/// the B.2 Worker serves from R2). Property names match the JSON
/// verbatim for System.Text.Json. Kept in Core so it is
/// Velopack-package-free and reachable from Ghostty.Tests.
/// </summary>
internal sealed record VelopackReleaseEntry(
    [property: JsonPropertyName("Version")] string Version,
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("FileName")] string FileName,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("SHA1")] string SHA1);

/// <summary>
/// Source-generated JSON metadata for the manifest payload. Lets
/// <c>WinttyManifestClient</c> deserialize under NativeAOT/trimming
/// without IL2026/IL3050 warnings.
/// </summary>
[JsonSerializable(typeof(List<VelopackReleaseEntry>))]
internal sealed partial class VelopackManifestJsonContext : JsonSerializerContext;
