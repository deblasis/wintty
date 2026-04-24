using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class DiscoveryCacheTests
{
    [Fact]
    public void Roundtrip_PreservesProfiles()
    {
        var original = new DiscoveryCacheFile(
            SchemaVersion: DiscoveryCache.CurrentSchemaVersion,
            WinttyVersion: "1.2.3",
            CreatedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
            Profiles: new List<DiscoveryCacheEntry>
            {
                new("cmd", "Command Prompt", "cmd.exe", "cmd", null, null, null),
                new("wsl-ubuntu", "WSL: Ubuntu", "wsl.exe -d Ubuntu", "wsl",
                    null, "wsl-distro:Ubuntu", null),
            });

        var bytes = DiscoveryCache.Serialize(original);
        var parsed = DiscoveryCache.Deserialize(bytes);

        Assert.NotNull(parsed);
        Assert.Equal(DiscoveryCache.CurrentSchemaVersion, parsed!.SchemaVersion);
        Assert.Equal(2, parsed.Profiles.Count);
        Assert.Equal("cmd", parsed.Profiles[0].Id);
    }

    [Fact]
    public void Deserialize_UnknownSchemaVersion_ReturnsNull()
    {
        var json = """{"schemaVersion":999,"winttyVersion":"x","createdAt":"2026-04-24T00:00:00+00:00","profiles":[]}""";
        var result = DiscoveryCache.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_GarbageBytes_ReturnsNull()
    {
        var result = DiscoveryCache.Deserialize(new byte[] { 0xFF, 0xFE, 0x01, 0x02 });
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_NullBytes_ReturnsNull()
    {
        var result = DiscoveryCache.Deserialize(null!);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_EmptyBytes_ReturnsNull()
    {
        var result = DiscoveryCache.Deserialize(System.Array.Empty<byte>());
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_NullProfilesField_ReturnsNull()
    {
        var json = $$"""{"schemaVersion":{{DiscoveryCache.CurrentSchemaVersion}},"winttyVersion":"x","createdAt":"2026-04-24T00:00:00+00:00","profiles":null}""";
        var result = DiscoveryCache.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Null(result);
    }
}
