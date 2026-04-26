using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Persisted discovery cache file shape. Keep the property names camelCased
/// via the naming policy below so the on-disk file is human-readable.
/// </summary>
public sealed record DiscoveryCacheFile(
    int SchemaVersion,
    string WinttyVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DiscoveryCacheEntry> Profiles);

public sealed record DiscoveryCacheEntry(
    string Id,
    string Name,
    string Command,
    string ProbeId,
    string? WorkingDirectory,
    string? IconToken,
    string? TabTitle);

/// <summary>
/// Serialize/deserialize the DiscoveryCacheFile. Uses source-gen to stay
/// AOT-clean (matches ScenarioDtoContext pattern). Unknown schema
/// versions are rejected (returns null) so the caller can discard and
/// re-discover. Malformed JSON returns null rather than throwing.
/// </summary>
public static class DiscoveryCache
{
    // v2: probe Command outputs now quote paths that contain spaces
    // (Git Bash bin path, PowerShell 7 install path, etc.). Pre-fix
    // caches stored unquoted commands that fail libghostty's argv
    // tokenizer. Bumping forces a re-discovery on first launch with
    // the fix.
    public const int CurrentSchemaVersion = 2;

    public static byte[] Serialize(DiscoveryCacheFile file)
        => JsonSerializer.SerializeToUtf8Bytes(file, DiscoveryCacheContext.Default.DiscoveryCacheFile);

    public static DiscoveryCacheFile? Deserialize(byte[] bytes)
    {
        if (bytes is null or { Length: 0 }) return null;
        try
        {
            var file = JsonSerializer.Deserialize(bytes, DiscoveryCacheContext.Default.DiscoveryCacheFile);
            if (file is null) return null;
            if (file.SchemaVersion != CurrentSchemaVersion) return null;
            if (file.Profiles is null) return null;
            return file;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DiscoveryCacheFile))]
internal partial class DiscoveryCacheContext : JsonSerializerContext
{
}
