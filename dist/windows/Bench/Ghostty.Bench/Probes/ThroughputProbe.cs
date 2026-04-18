using System.Diagnostics;
using Ghostty.Bench.Harness;
using Ghostty.Bench.Output;
using Ghostty.Bench.Transports;

namespace Ghostty.Bench.Probes;

public sealed class ThroughputProbe : Probe
{
    private const int Samples = 3;
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
        double[] mbps = new double[Samples];
        // Each Read() fills the buffer to readBuf.Length before we stop, so
        // we don't need to zero it between samples.
        byte[] readBuf = new byte[_payload.Length];

        for (int i = 0; i < Samples; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // transport.Output.Read is a blocking synchronous call that the
            // CTS cannot interrupt. If the 20s deadline fires, Wait throws
            // but the reader task stays parked until the transport is
            // disposed at the top of Program.Main. Program.RunAll then
            // kills the child, which is the real unblock mechanism. For
            // that reason we don't bother awaiting the task on the error
            // path.
            var readerTask = Task.Run(() =>
            {
                int read = 0;
                while (read < readBuf.Length)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    int n = transport.Output.Read(readBuf, read, readBuf.Length - read);
                    if (n == 0) throw new EndOfStreamException("peer closed during throughput read");
                    read += n;
                }
            }, cts.Token);

            long start = Stopwatch.GetTimestamp();
            transport.Input.Write(_payload.Span);
            transport.Input.Flush();
            readerTask.Wait(cts.Token);
            long elapsedTicks = Stopwatch.GetTimestamp() - start;

            double seconds = elapsedTicks / (double)Stopwatch.Frequency;
            double megabytes = _payload.Length / (1024.0 * 1024.0);
            mbps[i] = megabytes / seconds;
        }

        Array.Sort(mbps);
        return ResultJson.Throughput(
            probe: Name,
            transport: TransportLabel,
            payload: PayloadLabel,
            payloadBytes: _payload.Length,
            p50Mbps: Math.Round(mbps[Samples / 2], 2),
            minMbps: Math.Round(mbps[0], 2),
            maxMbps: Math.Round(mbps[Samples - 1], 2),
            samples: Samples,
            host: host,
            timestampUtc: timestampUtc);
    }
}
