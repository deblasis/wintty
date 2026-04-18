using System.Diagnostics;
using Ghostty.Bench.Harness;
using Xunit;

namespace Ghostty.Tests.Bench;

public class HarnessTests
{
    [Fact]
    public void RunRoundTrip_RunsExactIterationCount()
    {
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 10, samples: 50);
        Assert.Equal(50, timings.Length);
    }

    [Fact]
    public void RunRoundTrip_WarmupIsExcludedFromTimings()
    {
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 10, samples: 50);
        Assert.Equal(50, timings.Length);
        // Cannot assert on absolute times because this is a loopback fake,
        // but we can assert the count is samples only, not warmup+samples.
    }

    [Fact]
    public void RunRoundTrip_AllTimingsAreNonNegative()
    {
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 5, samples: 20);
        foreach (var ticks in timings)
        {
            Assert.True(ticks >= 0, $"timing was negative: {ticks}");
        }
    }

    [Fact]
    public void RunRoundTrip_ThrowsOnZeroSamples()
    {
        using var t = new FakeTransport();
        Assert.Throws<ArgumentOutOfRangeException>(() => Runner.RunRoundTrip(t, warmup: 10, samples: 0));
    }

    [Fact]
    public void SentinelRoundTrip_FindsPayloadAfterVtNoise()
    {
        // Script: on each write, respond with (ESC noise, sentinel, trailing).
        // Simulates conhost's real shape: preamble-like VT, the echoed
        // "!~" payload, then trailing VT state bytes that don't contain "!~".
        byte[] vtPrefix = [0x1B, (byte)'[', (byte)'2', (byte)'5', (byte)'l']; // CSI 25 l
        byte[] sentinel = "!~"u8.ToArray();
        byte[] trailing = [0x1B, (byte)'[', (byte)'m']; // CSI m (SGR reset)
        byte[] response = [.. vtPrefix, .. sentinel, .. trailing];

        using var t = new FakeTransport(_ => response);

        long[] timings = Runner.RunRoundTrip(t, warmup: 2, samples: 5);

        Assert.Equal(5, timings.Length);
        foreach (var ticks in timings)
        {
            Assert.True(ticks >= 0, $"non-negative timing expected, got {ticks}");
        }
    }

    [Fact]
    public void SentinelRoundTrip_PositiveTicksUnderReasonableBound()
    {
        // In-process FakeTransport echo round-trip should be microseconds
        // to low milliseconds; assert a generous 1s ceiling.
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 2, samples: 10);

        long oneSecondInTicks = Stopwatch.Frequency;
        foreach (var ticks in timings)
        {
            Assert.True(ticks < oneSecondInTicks,
                $"unexpectedly slow round-trip: {ticks} ticks (~{ticks * 1000.0 / Stopwatch.Frequency:F2} ms)");
        }
    }

    // Skipped: scripted-responder delivers one burst per input write; once
    // that burst is drained, subsequent Reads block indefinitely, so the
    // harness's per-iteration deadline (evaluated between Reads) never fires
    // and the test would hang instead of exercising the TimeoutException
    // path. Under real ConPTY the conhost keeps emitting VT state bytes so
    // the deadline is exercised naturally. If scripted mode ever grows a
    // streaming-responder variant, un-skip this.
    [Fact(Skip = "scripted responder is one-shot per input write; deadline path is exercised by real ConPTY, not FakeTransport")]
    public void SentinelRoundTrip_ThrowsOnPerIterationDeadline()
    {
    }

    [Fact]
    public void SentinelRoundTrip_ThrowsOnEndOfStream()
    {
        // Script: first write gets null response, which closes Output.
        using var t = new FakeTransport(_ => null);

        Assert.Throws<EndOfStreamException>(() =>
            Runner.RunRoundTrip(t, warmup: 0, samples: 1));
    }
}
