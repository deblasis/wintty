using System.Text.Json;
using Ghostty.Bench.Output;
using Xunit;

namespace Ghostty.Tests.Bench;

public class ResultJsonTests
{
    private static HostInfo FixedHost() =>
        new("Windows", "26200", "Test CPU", "x64", "10.0.0", "inbox");

    [Fact]
    public void RoundTripProbe_SerializesExpectedSchema()
    {
        var r = ResultJson.RoundTrip(
            probe: "conpty_roundtrip",
            transport: "conpty",
            p50Us: 318.4,
            p95Us: 491.2,
            p99Us: 802.6,
            samples: 1000,
            warmup: 100,
            host: FixedHost(),
            timestampUtc: new DateTime(2026, 4, 17, 14, 22, 11, DateTimeKind.Utc));

        string json = ResultJson.Serialize(r);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("conpty_roundtrip", root.GetProperty("probe").GetString());
        Assert.Equal("microseconds", root.GetProperty("unit").GetString());
        Assert.Equal(318.4, root.GetProperty("p50_us").GetDouble());
        Assert.Equal(491.2, root.GetProperty("p95_us").GetDouble());
        Assert.Equal(802.6, root.GetProperty("p99_us").GetDouble());
        Assert.Equal(1000, root.GetProperty("samples").GetInt32());
        Assert.Equal(100, root.GetProperty("warmup").GetInt32());
        Assert.Equal("conpty", root.GetProperty("transport").GetString());
        Assert.Equal("Ghostty.Bench.EchoChild", root.GetProperty("child").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("payload").ValueKind);
        Assert.Equal("x64", root.GetProperty("host").GetProperty("arch").GetString());
        Assert.Equal("inbox", root.GetProperty("host").GetProperty("conpty_source").GetString());
        Assert.Equal("2026-04-17T14:22:11Z", root.GetProperty("timestamp_utc").GetString());
        Assert.Equal("1", root.GetProperty("bench_version").GetString());
        // WhenWritingNull must suppress throughput-family fields in a round-trip result.
        Assert.False(root.TryGetProperty("ingest_p50_mbps", out _));
        Assert.False(root.TryGetProperty("ingest_min_mbps", out _));
        Assert.False(root.TryGetProperty("ingest_max_mbps", out _));
        Assert.False(root.TryGetProperty("emit_p50_mbps", out _));
        Assert.False(root.TryGetProperty("emit_min_mbps", out _));
        Assert.False(root.TryGetProperty("emit_max_mbps", out _));
        Assert.False(root.TryGetProperty("payload_bytes", out _));
    }

    [Fact]
    public void ThroughputProbe_SerializesExpectedSchema()
    {
        var r = ResultJson.Throughput(
            probe: "conpty_throughput_sgr",
            transport: "conpty",
            payload: "sgr",
            payloadBytes: 100 * 1024 * 1024,
            ingestP50Mbps: 87.3,
            ingestMinMbps: 82.1,
            ingestMaxMbps: 91.4,
            emitP50Mbps: 6.5,
            emitMinMbps: 6.1,
            emitMaxMbps: 7.0,
            samples: 3,
            host: FixedHost(),
            timestampUtc: new DateTime(2026, 4, 17, 14, 22, 11, DateTimeKind.Utc));

        string json = ResultJson.Serialize(r);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("conpty_throughput_sgr", root.GetProperty("probe").GetString());
        Assert.Equal("megabytes_per_second", root.GetProperty("unit").GetString());
        Assert.Equal(87.3, root.GetProperty("ingest_p50_mbps").GetDouble());
        Assert.Equal(82.1, root.GetProperty("ingest_min_mbps").GetDouble());
        Assert.Equal(91.4, root.GetProperty("ingest_max_mbps").GetDouble());
        Assert.Equal(6.5, root.GetProperty("emit_p50_mbps").GetDouble());
        Assert.Equal(6.1, root.GetProperty("emit_min_mbps").GetDouble());
        Assert.Equal(7.0, root.GetProperty("emit_max_mbps").GetDouble());
        Assert.Equal(3, root.GetProperty("samples").GetInt32());
        Assert.Equal(104857600L, root.GetProperty("payload_bytes").GetInt64());
        Assert.Equal("sgr", root.GetProperty("payload").GetString());
        Assert.Equal("1", root.GetProperty("bench_version").GetString());
        // WhenWritingNull must suppress round-trip-family fields in a throughput result.
        Assert.False(root.TryGetProperty("p50_us", out _));
        Assert.False(root.TryGetProperty("p95_us", out _));
        Assert.False(root.TryGetProperty("p99_us", out _));
        Assert.False(root.TryGetProperty("warmup", out _));
        // The old single-triple field names must no longer appear.
        Assert.False(root.TryGetProperty("p50_mbps", out _));
        Assert.False(root.TryGetProperty("min_mbps", out _));
        Assert.False(root.TryGetProperty("max_mbps", out _));
    }

    [Fact]
    public void TimestampUtc_ParsesAsIso8601()
    {
        var r = ResultJson.RoundTrip("x", "conpty", 1, 1, 1, 1, 0,
            FixedHost(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string json = ResultJson.Serialize(r);
        using var doc = JsonDocument.Parse(json);
        string ts = doc.RootElement.GetProperty("timestamp_utc").GetString()!;
        var parsed = DateTime.Parse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }
}
