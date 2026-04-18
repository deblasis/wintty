using System.Diagnostics;
using System.Runtime.InteropServices;
using Ghostty.Bench.Transports;

namespace Ghostty.Bench.Harness;

// Renamed from `Harness` to avoid collision with the containing namespace,
// which forced callers in `Ghostty.Bench.Probes` into `global::Ghostty.Bench.Harness.Harness.X`.
public static class Runner
{
    // 2-byte sentinel payload. The bigram "!~" is unlikely to appear
    // adjacent in conhost's VT output stream: '!' (0x21) is a VT
    // intermediate byte, '~' (0x7E) is a final byte, and the only
    // standard CSI sequence that pairs them is DECSTR (CSI ! ~), which
    // conhost does not emit during normal screen updates. See the spec
    // for the full analysis.
    private static readonly byte[] Payload = "!~"u8.ToArray();

    // Per-iteration deadline for finding the echoed sentinel in the output
    // stream. Set deliberately wide (~30x a warm-machine ConPTY round-trip)
    // so cold-cache first iterations don't trip it, but tight enough to
    // catch a stuck pipe within one iteration instead of burning the whole
    // watchdog budget.
    private static readonly TimeSpan PerIterationDeadline = TimeSpan.FromSeconds(1);

    // Performs `warmup` untimed sentinel round-trips, then `samples`
    // timed round-trips, and returns the timings array in Stopwatch
    // ticks. Caller converts to microseconds via TicksToMicroseconds.
    public static long[] RunRoundTrip(ITransport transport, int warmup, int samples)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        byte[] scratch = new byte[1024];

        for (int i = 0; i < warmup; i++)
        {
            SentinelRoundTrip(transport, scratch);
        }

        long[] timings = new long[samples];
        for (int i = 0; i < samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            SentinelRoundTrip(transport, scratch);
            long end = Stopwatch.GetTimestamp();
            timings[i] = end - start;
        }

        return timings;
    }

    // Writes the 2-byte sentinel payload to the transport's Input, then
    // reads into `scratch` in a loop, scanning the accumulated bytes for
    // the sentinel pattern. Returns when found. Throws:
    //  - EndOfStreamException if Output reaches EOF before the sentinel.
    //  - TimeoutException if PerIterationDeadline elapses without a match.
    //
    // `scratch` is reused across iterations as an output-side work buffer;
    // a fresh per-iteration window list handles accumulation because
    // conhost's VT emission can span multiple reads.
    private static void SentinelRoundTrip(ITransport t, byte[] scratch)
    {
        t.Input.Write(Payload);
        t.Input.Flush();

        long deadline = Stopwatch.GetTimestamp()
                        + (long)(PerIterationDeadline.TotalSeconds * Stopwatch.Frequency);

        // Per-iteration window. Small starting capacity; grows on demand.
        // Reset each iteration so leftover conhost trailing bytes from the
        // previous iteration cannot contribute a stale match. The actual
        // kernel pipe buffer still contains those bytes; they get read into
        // the fresh window here but do not contain the "!~" pattern (conhost
        // trailing is SGR / cursor VT state, not printable content).
        var window = new List<byte>(64);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            int n = t.Output.Read(scratch, 0, scratch.Length);
            if (n == 0)
            {
                throw new EndOfStreamException("peer closed during sentinel round-trip read");
            }

            window.AddRange(scratch.AsSpan(0, n));

            if (CollectionsMarshal.AsSpan(window).IndexOf(Payload.AsSpan()) >= 0)
            {
                return;
            }
        }

        throw new TimeoutException(
            $"sentinel not seen within {PerIterationDeadline.TotalSeconds:F1}s");
    }

    public static double TicksToMicroseconds(long ticks) =>
        ticks * 1_000_000.0 / Stopwatch.Frequency;
}
