using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Bench.Output;

// Unified result type so round-trip and throughput share a single
// JSON writer. Fields that don't apply to a given probe family are
// emitted as null and the serializer suppresses them.
public sealed record ResultJson(
    [property: JsonPropertyName("probe")] string Probe,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("p50_us")] double? P50Us,
    [property: JsonPropertyName("p95_us")] double? P95Us,
    [property: JsonPropertyName("p99_us")] double? P99Us,
    [property: JsonPropertyName("p50_mbps")] double? P50Mbps,
    [property: JsonPropertyName("min_mbps")] double? MinMbps,
    [property: JsonPropertyName("max_mbps")] double? MaxMbps,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("warmup")] int? Warmup,
    [property: JsonPropertyName("payload_bytes")] long? PayloadBytes,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("child")] string Child,
    [property: JsonPropertyName("payload"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Payload,
    [property: JsonPropertyName("host")] HostInfo Host,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("bench_version")] string BenchVersion)
{
    public const string CurrentVersion = "1";
    public const string ChildName = "Ghostty.Bench.EchoChild";

    private static readonly JsonSerializerOptions _compactOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions _indentedOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static ResultJson RoundTrip(
        string probe,
        string transport,
        double p50Us,
        double p95Us,
        double p99Us,
        int samples,
        int warmup,
        HostInfo host,
        DateTime timestampUtc) =>
        new(
            Probe: probe,
            Unit: "microseconds",
            P50Us: p50Us, P95Us: p95Us, P99Us: p99Us,
            P50Mbps: null, MinMbps: null, MaxMbps: null,
            Samples: samples,
            Warmup: warmup,
            PayloadBytes: null,
            Transport: transport,
            Child: ChildName,
            Payload: null,
            Host: host,
            TimestampUtc: FormatUtc(timestampUtc),
            BenchVersion: CurrentVersion);

    public static ResultJson Throughput(
        string probe,
        string transport,
        string payload,
        long payloadBytes,
        double p50Mbps,
        double minMbps,
        double maxMbps,
        int samples,
        HostInfo host,
        DateTime timestampUtc) =>
        new(
            Probe: probe,
            Unit: "megabytes_per_second",
            P50Us: null, P95Us: null, P99Us: null,
            P50Mbps: p50Mbps, MinMbps: minMbps, MaxMbps: maxMbps,
            Samples: samples,
            Warmup: null,
            PayloadBytes: payloadBytes,
            Transport: transport,
            Child: ChildName,
            Payload: payload,
            Host: host,
            TimestampUtc: FormatUtc(timestampUtc),
            BenchVersion: CurrentVersion);

    private static string FormatUtc(DateTime t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    public static string Serialize(ResultJson r) =>
        JsonSerializer.Serialize(r, _compactOptions);

    public static string SerializeIndented(ResultJson r) =>
        JsonSerializer.Serialize(r, _indentedOptions);
}
