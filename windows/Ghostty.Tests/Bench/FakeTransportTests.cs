using System.Diagnostics;
using Xunit;

namespace Ghostty.Tests.Bench;

public class FakeTransportTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(5);

    // Regression for a proven suite-wide deadlock. With the zero-size pipe
    // buffers the default NamedPipeServerStream constructor passes, a write
    // only completes against an outstanding reader. When the harness reader
    // cancelled before its first read (threadpool delay under full-suite
    // contention), the IO thread blocked forever in its first scripted
    // response write. This test pins the opposite: the write must land in
    // the kernel buffer even though NOTHING has read the output pipe yet.
    [Fact]
    public void ScriptedResponseWrite_CompletesWithoutAnyReader()
    {
        int responderCalls = 0;
        using var t = new FakeTransport(_ =>
        {
            Interlocked.Increment(ref responderCalls);
            return new byte[] { 0x01 };
        });

        // First input chunk on the test thread: the IO thread is parked in
        // its first Read, so this always completes. Its response write is the
        // write-before-read under test.
        t.Input.Write(new byte[] { 0x00 }, 0, 1);
        t.Input.Flush();

        // Second input chunk from a background task: pre-fix the IO thread
        // never comes back for it (stuck in the first response write), so
        // this write blocks. Keeping it off the test thread lets the poll
        // below fail the test inside the bound instead of hanging the suite.
        var secondWrite = Task.Run(() =>
        {
            t.Input.Write(new byte[] { 0x00 }, 0, 1);
            t.Input.Flush();
        });

        // The responder only runs its second call after the first response
        // write completed with zero readers on the output pipe.
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref responderCalls) < 2 && sw.Elapsed < BoundedWait)
            Thread.Sleep(10);

        Assert.True(Volatile.Read(ref responderCalls) >= 2,
            $"scripted response write never completed without a reader within {BoundedWait.TotalSeconds:F0}s");

        // Drain both responses so the read side is proven too, not just the
        // responder count. Both bytes are already in the kernel buffer, so
        // the synchronous reads return without blocking.
        byte[] received = new byte[2];
        int read = 0;
        while (read < received.Length)
        {
            int n = t.Output.Read(received, read, received.Length - read);
            Assert.True(n > 0, "output pipe reached EOF before both responses were drained");
            read += n;
        }
        Assert.Equal((byte)0x01, received[0]);
        Assert.Equal((byte)0x01, received[1]);
    }
}
