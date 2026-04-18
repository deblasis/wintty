using System.Diagnostics;
using Ghostty.Bench.Transports;

namespace Ghostty.Bench.Harness;

// Renamed from `Harness` to avoid collision with the containing namespace,
// which forced callers in `Ghostty.Bench.Probes` into `global::Ghostty.Bench.Harness.Harness.X`.
public static class Runner
{
    // Performs `warmup` untimed single-byte round-trips, then `samples`
    // timed round-trips, and returns the timings array in Stopwatch
    // ticks. Caller converts to microseconds via TicksToMicroseconds.
    public static long[] RunRoundTrip(ITransport transport, int warmup, int samples)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        Span<byte> writeBuf = stackalloc byte[1];
        writeBuf[0] = 0x42;
        Span<byte> readBuf = stackalloc byte[1];

        for (int i = 0; i < warmup; i++)
        {
            SingleByteRoundTrip(transport, writeBuf, readBuf);
        }

        long[] timings = new long[samples];
        for (int i = 0; i < samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            SingleByteRoundTrip(transport, writeBuf, readBuf);
            long end = Stopwatch.GetTimestamp();
            timings[i] = end - start;
        }

        return timings;
    }

    private static void SingleByteRoundTrip(ITransport t, ReadOnlySpan<byte> writeBuf, Span<byte> readBuf)
    {
        t.Input.Write(writeBuf);
        t.Input.Flush();
        int read = t.Output.Read(readBuf);
        if (read != 1)
        {
            throw new IOException($"expected 1 byte on round-trip read, got {read}");
        }
    }

    public static double TicksToMicroseconds(long ticks) =>
        ticks * 1_000_000.0 / Stopwatch.Frequency;
}
