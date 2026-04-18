using System.Diagnostics;
using System.Text;
using Ghostty.Bench.Harness;
using Ghostty.Bench.Output;
using Ghostty.Bench.Transports;

namespace Ghostty.Bench.Probes;

public sealed class ThroughputProbe : Probe
{
    private const int Samples = 3;

    // 60 s per iteration. At ConPTY's measured scroll-ASCII throughput of
    // ~100 KB/s, 1 MB takes ~10 s. 60 s gives 6x headroom for slow
    // machines, SGR / stress parser cost, and cold-start warmup. Tight
    // enough to catch a stuck pipe within one iteration instead of
    // burning the outer harness watchdog.
    private static readonly TimeSpan IterationDeadline = TimeSpan.FromSeconds(60);

    // 64 KB read buffer. Large enough to amortize Read syscalls over
    // 1 MB payloads while staying small enough that the per-iteration
    // scratch footprint is negligible.
    private const int ScratchSize = 64 * 1024;

    private readonly ReadOnlyMemory<byte> _payload;

    public ThroughputProbe(string name, string transport, string payloadLabel, ReadOnlyMemory<byte> payload)
    {
        Name = name;
        TransportLabel = transport;
        PayloadLabel = payloadLabel;
        _payload = payload;
    }

    public override string Name { get; }
    public string TransportLabel { get; }
    public string PayloadLabel { get; }

    public override ResultJson Run(ITransport transport, HostInfo host, DateTime timestampUtc)
    {
        double[] ingestMBps = new double[Samples];
        double[] emitMBps = new double[Samples];
        byte[] scratch = new byte[ScratchSize];

        for (int i = 0; i < Samples; i++)
        {
            // Fresh 16-hex-char nonce per iteration. Guid.NewGuid() is v4
            // (random); 64 bits of nonce entropy rules out any accidental
            // match from a leftover terminator lingering in conhost's
            // screen buffer between iterations.
            string nonce = Guid.NewGuid().ToString("N").Substring(0, 16);
            byte[] terminator = Encoding.ASCII.GetBytes("\r\n~ENDOFBURST_" + nonce + "~");

            (long elapsedTicks, long emitBytes) = Runner.RunThroughputIteration(
                transport: transport,
                payload: _payload,
                terminator: terminator,
                deadline: IterationDeadline,
                scratch: scratch);

            double seconds = elapsedTicks / (double)Stopwatch.Frequency;
            double payloadMB = _payload.Length / (1024.0 * 1024.0);
            double emitMB = emitBytes / (1024.0 * 1024.0);

            // Ingest counts only the payload bytes: the 31-byte terminator
            // is scan infrastructure, not user data. Emit counts every byte
            // read during the window, including any that arrived alongside
            // the terminator in the final read; at 1 MB scale that is
            // noise.
            ingestMBps[i] = payloadMB / seconds;
            emitMBps[i] = emitMB / seconds;
        }

        Array.Sort(ingestMBps);
        Array.Sort(emitMBps);

        return ResultJson.Throughput(
            probe: Name,
            transport: TransportLabel,
            payload: PayloadLabel,
            payloadBytes: _payload.Length,
            ingestP50Mbps: Math.Round(ingestMBps[Samples / 2], 2),
            ingestMinMbps: Math.Round(ingestMBps[0], 2),
            ingestMaxMbps: Math.Round(ingestMBps[Samples - 1], 2),
            emitP50Mbps:   Math.Round(emitMBps[Samples / 2], 2),
            emitMinMbps:   Math.Round(emitMBps[0], 2),
            emitMaxMbps:   Math.Round(emitMBps[Samples - 1], 2),
            samples: Samples,
            host: host,
            timestampUtc: timestampUtc);
    }
}
