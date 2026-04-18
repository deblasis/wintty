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

    // --- RunThroughputIteration tests ---

    [Fact]
    public void RunThroughputIteration_ReturnsElapsedAndEmitBytes()
    {
        // Echo FakeTransport: every byte written comes back. Payload +
        // terminator round-trip byte-for-byte, so emit must equal
        // payload.Length + terminator.Length.
        using var t = new FakeTransport();
        byte[] payload = new byte[4096];
        new Random(42).NextBytes(payload);
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + Guid.NewGuid().ToString("N").Substring(0, 16) + "~");
        byte[] scratch = new byte[64 * 1024];

        var (elapsedTicks, emitBytes) = Runner.RunThroughputIteration(
            t, payload, terminator, TimeSpan.FromSeconds(5), scratch);

        Assert.True(elapsedTicks > 0, "elapsed must be positive");
        Assert.Equal(payload.Length + terminator.Length, emitBytes);
    }

    [Fact]
    public void RunThroughputIteration_DetectsTerminatorSplitAcrossReads()
    {
        // Scripted FakeTransport that returns bytes in two chunks, splitting
        // the terminator across the boundary. Exercises the carryover scan.
        byte[] payload = new byte[1024];
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + Guid.NewGuid().ToString("N").Substring(0, 16) + "~");
        byte[] combined = new byte[payload.Length + terminator.Length];
        Buffer.BlockCopy(payload, 0, combined, 0, payload.Length);
        Buffer.BlockCopy(terminator, 0, combined, payload.Length, terminator.Length);

        int splitPoint = payload.Length + 15;   // mid-terminator
        bool firstCall = true;
        using var t = new FakeTransport(_ =>
        {
            if (firstCall)
            {
                firstCall = false;
                byte[] first = new byte[splitPoint];
                Buffer.BlockCopy(combined, 0, first, 0, splitPoint);
                return first;
            }
            byte[] second = new byte[combined.Length - splitPoint];
            Buffer.BlockCopy(combined, splitPoint, second, 0, second.Length);
            return second;
        });
        byte[] scratch = new byte[64 * 1024];

        var (_, emitBytes) = Runner.RunThroughputIteration(
            t, payload, terminator, TimeSpan.FromSeconds(5), scratch);

        Assert.Equal(combined.Length, emitBytes);
    }

    [Fact]
    public void RunThroughputIteration_ThrowsOnEndOfStreamBeforeTerminator()
    {
        // Scripted transport returns a short buffer that does NOT contain the
        // terminator, then null (closes output). Harness must surface EOS.
        byte[] payload = new byte[256];
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + Guid.NewGuid().ToString("N").Substring(0, 16) + "~");
        int call = 0;
        using var t = new FakeTransport(_ =>
        {
            call++;
            if (call == 1) return payload;   // no terminator
            return null;                     // close output
        });
        byte[] scratch = new byte[64 * 1024];

        Assert.Throws<EndOfStreamException>(() =>
            Runner.RunThroughputIteration(
                t, payload, terminator, TimeSpan.FromSeconds(5), scratch));
    }

    [Fact]
    public void RunThroughputIteration_ThrowsOnDeadlineExceeded()
    {
        // Scripted transport returns a one-byte buffer and then blocks (the
        // responder lambda never returns for the second call). Deadline of
        // 250 ms must fire well before the xunit-level timeout.
        byte[] payload = new byte[64];
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + Guid.NewGuid().ToString("N").Substring(0, 16) + "~");
        int call = 0;
        var gate = new ManualResetEventSlim(initialState: false);
        using var t = new FakeTransport(_ =>
        {
            call++;
            if (call == 1) return new byte[] { 0x00 };
            gate.Wait(TimeSpan.FromSeconds(10));   // released at test end
            return new byte[0];
        });
        byte[] scratch = new byte[64 * 1024];

        // Harness converts the internal OCE from the cancelled ReadAsync
        // into TimeoutException. Asserting the strict type catches a
        // regression if the conversion ever silently leaks.
        Assert.Throws<TimeoutException>(() =>
            Runner.RunThroughputIteration(
                t, payload, terminator, TimeSpan.FromMilliseconds(250), scratch));

        gate.Set();   // let the scripted responder unwind
    }

    [Fact]
    public void RunThroughputIteration_IgnoresWrongNonceMatch()
    {
        // A terminator with a different nonce must not match. Scripted
        // responder emits a wrong-nonce terminator first, then the correct
        // terminator. emit must cover both.
        byte[] payload = new byte[256];
        string nonce = Guid.NewGuid().ToString("N").Substring(0, 16);
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + nonce + "~");
        byte[] wrongTerminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_aaaaaaaaaaaaaaaa~");
        int call = 0;
        using var t = new FakeTransport(_ =>
        {
            call++;
            if (call == 1) return wrongTerminator;
            if (call == 2) return terminator;
            return null;
        });
        byte[] scratch = new byte[64 * 1024];

        var (_, emitBytes) = Runner.RunThroughputIteration(
            t, payload, terminator, TimeSpan.FromSeconds(5), scratch);

        Assert.Equal(wrongTerminator.Length + terminator.Length, emitBytes);
    }

    [Fact]
    public void RunThroughputIteration_CarryoverDoesNotOverrunScratch()
    {
        // Guards the scratch.Length - carryoverLen invariant: scratch must
        // always have room for at least one byte after tail carryover. Uses
        // a small scratch (128 bytes) so any off-by-one in the carryover
        // shift shows up as an ArgumentOutOfRangeException.
        byte[] payload = new byte[4096];
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + Guid.NewGuid().ToString("N").Substring(0, 16) + "~");
        using var t = new FakeTransport();   // echo mode
        byte[] smallScratch = new byte[128];

        var (_, emitBytes) = Runner.RunThroughputIteration(
            t, payload, terminator, TimeSpan.FromSeconds(5), smallScratch);

        Assert.Equal(payload.Length + terminator.Length, emitBytes);
    }
}
