using System.Text;
using System.Text.Json;
using Ghostty.Bench.Output;
using Ghostty.Bench.Probes;
using Xunit;

namespace Ghostty.Tests.Bench;

public class ThroughputProbeTests
{
    // Each iteration must use a distinct nonce. Guards against leftover-
    // match false positives under ConPTY where a prior iteration's
    // terminator can linger in conhost's screen buffer.
    [Fact]
    public void ThroughputProbe_RegeneratesNoncePerIteration()
    {
        var capturedWrites = new List<byte[]>();
        using var t = new FakeTransport(
            input =>
            {
                capturedWrites.Add(input.ToArray());
                // Echo back so the probe's reader loop terminates.
                return input.ToArray();
            });

        byte[] payload = new byte[64];
        var probe = new ThroughputProbe("test", "fake", "tiny", payload);

        var host = new HostInfo("Windows", "26200", "Test CPU", "x64", "10.0.0", "inbox");
        probe.Run(t, host, DateTime.UtcNow);

        // Scan every captured write for terminator occurrences and collect
        // the nonces. The probe writes payload and terminator as two separate
        // Input.Write calls; depending on FakeTransport pipe scheduling the
        // server-side IoLoop may read them as one or two chunks per iteration.
        // Either way the terminator prefix lands in some captured write, and
        // three iterations must produce three distinct nonces.
        var nonces = new HashSet<string>();
        byte[] prefix = Encoding.ASCII.GetBytes("\r\n~ENDOFBURST_");
        foreach (byte[] w in capturedWrites)
        {
            int idx = w.AsSpan().IndexOf(prefix);
            if (idx < 0) continue;
            if (idx + prefix.Length + 16 > w.Length) continue;
            nonces.Add(Encoding.ASCII.GetString(w, idx + prefix.Length, 16));
        }
        Assert.Equal(3, nonces.Count);
    }

    // The probe must report both ingest and emit numbers. Under echo mode,
    // ingest and emit differ only by terminator overhead (~31 bytes out of
    // 65 536), so they are approximately equal.
    [Fact]
    public void ThroughputProbe_ReportsBothIngestAndEmitMetrics()
    {
        using var t = new FakeTransport();   // echo
        byte[] payload = new byte[64 * 1024];
        new Random(1337).NextBytes(payload);
        var probe = new ThroughputProbe("test", "fake", "medium", payload);

        var host = new HostInfo("Windows", "26200", "Test CPU", "x64", "10.0.0", "inbox");
        var result = probe.Run(t, host, DateTime.UtcNow);

        // All six rate fields must be non-null and positive.
        Assert.NotNull(result.IngestP50Mbps);
        Assert.NotNull(result.IngestMinMbps);
        Assert.NotNull(result.IngestMaxMbps);
        Assert.NotNull(result.EmitP50Mbps);
        Assert.NotNull(result.EmitMinMbps);
        Assert.NotNull(result.EmitMaxMbps);
        Assert.True(result.IngestP50Mbps!.Value > 0);
        Assert.True(result.EmitP50Mbps!.Value > 0);

        // For echo mode, emit ~ ingest * (1 + terminator/payload). With a
        // 64 KB payload and ~31 B terminator, emit is within ~1 % of ingest.
        double ratio = result.EmitP50Mbps!.Value / result.IngestP50Mbps!.Value;
        Assert.InRange(ratio, 0.95, 1.10);
    }

    // The probe must write payload first, then terminator. Scripted
    // responder captures all input writes and the assertion validates the
    // byte order.
    [Fact]
    public void ThroughputProbe_WritesPayloadThenTerminator()
    {
        var captured = new List<byte>();
        using var t = new FakeTransport(input =>
        {
            captured.AddRange(input.ToArray());
            return input.ToArray();
        });

        byte[] payload = Encoding.ASCII.GetBytes("HELLO");
        var probe = new ThroughputProbe("test", "fake", "tiny", payload);
        var host = new HostInfo("Windows", "26200", "Test CPU", "x64", "10.0.0", "inbox");
        probe.Run(t, host, DateTime.UtcNow);

        // Across the full 3-iteration capture, the first 5 bytes must be
        // the payload "HELLO". The terminator follows starting at offset 5.
        Assert.True(captured.Count >= 5 + 31, $"captured too few bytes: {captured.Count}");
        string firstFive = Encoding.ASCII.GetString(captured.GetRange(0, 5).ToArray());
        Assert.Equal("HELLO", firstFive);

        // Terminator starts with "\r\n~ENDOFBURST_" at offset 5.
        string prefix = Encoding.ASCII.GetString(captured.GetRange(5, 14).ToArray());
        Assert.Equal("\r\n~ENDOFBURST_", prefix);
    }
}
