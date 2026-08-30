using System.Diagnostics;
using Xunit;

namespace Ghostty.Tests.Bench;

public class FakeTransportTests
{
    // Generous on purpose. The deadlock this suite once hit blocked forever,
    // so any bound catches it; the bound exists to keep a broken suite
    // failing fast, not to measure latency. Each stage gets its OWN budget
    // and stopwatch: a shared one converts slow-but-innocent latency
    // anywhere in front of the second gate into a false "write never
    // completed" verdict, and the failure messages below carry each stage's
    // timing so the next flake says where the budget went.
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(20);

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

        // Wait for the responder's FIRST call before writing again. Issuing
        // the second write while the IO thread was still parking into its
        // first Read let both writes coalesce into one (observed as a single
        // two-byte Read under full-suite contention), so the responder ran
        // once and the count below could never reach 2. The responder runs
        // BEFORE the response write, so at first call the IO thread holds no
        // reader on the output pipe: the write-under-test is already in
        // flight, which is the state this test exists to pin.
        var firstCall = Stopwatch.StartNew();
        while (Volatile.Read(ref responderCalls) < 1 && firstCall.Elapsed < BoundedWait)
            Thread.Sleep(10);
        var firstCallMs = firstCall.ElapsedMilliseconds;

        Assert.True(Volatile.Read(ref responderCalls) >= 1,
            $"scripted responder never ran within {BoundedWait.TotalSeconds:F0}s");

        // Second input chunk on its own dedicated thread, deliberately not
        // Task.Run: with the pool saturated by blocked workers (the shape a
        // full-suite run can approach), a single pool item has measured
        // 14.5s to dispatch where a dedicated thread started in 7ms, and
        // pool dispatch is the one latency here no test bound can cover.
        // Pre-buffer-fix, the IO thread stuck in the first response write
        // never came back for this byte, so the write below blocks forever
        // and the poll after it fails inside the bound instead of hanging
        // the suite.
        Exception? writeError = null;
        var secondWrite = new Thread(() =>
        {
            try
            {
                t.Input.Write(new byte[] { 0x00 }, 0, 1);
                t.Input.Flush();
            }
            catch (Exception e)
            {
                writeError = e;
            }
        })
        {
            IsBackground = true,
            Name = "FakeTransportTests.SecondWrite",
        };
        secondWrite.Start();

        // The responder only runs its second call after the first response
        // write completed with zero readers on the output pipe. Timed from
        // here with its own stopwatch: this stage's job is to prove the
        // write-before-read completes, and it gets its own full budget.
        var secondCall = Stopwatch.StartNew();
        while (Volatile.Read(ref responderCalls) < 2 && secondCall.Elapsed < BoundedWait)
            Thread.Sleep(10);

        Assert.True(Volatile.Read(ref responderCalls) >= 2,
            $"scripted response write never completed without a reader within {BoundedWait.TotalSeconds:F0}s " +
            $"(first responder call after {firstCallMs}ms; second write " +
            $"{(writeError is null ? "still in flight" : $"threw: {writeError.Message}")})");

        // Drain both responses so the read side is proven too, not just the
        // responder count. The responder counts its call before the matching
        // response write, so the second byte may land a beat after the gate
        // above opens; it is one byte into an empty 64KB buffer, so the
        // reads below complete without waiting on any writer.
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

        // The gate above proves the second byte reached the pipe, so the
        // writer has finished its work; observe its outcome so a faulted
        // write fails here instead of vanishing into an unobserved thread.
        Assert.True(secondWrite.Join(TimeSpan.FromSeconds(5)),
            "second write thread never finished even though its byte was drained");
        Assert.Null(writeError);
    }
}
