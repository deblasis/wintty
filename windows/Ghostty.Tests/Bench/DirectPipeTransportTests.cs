using System.Diagnostics;
using System.Runtime.Versioning;
using Ghostty.Bench.Transports;
using Xunit;

namespace Ghostty.Tests.Bench;

public class DirectPipeTransportTests
{
    // DirectPipe has no conhost-style preamble to drain, so WaitReady must
    // return immediately. This test pins the no-op contract so a future
    // change to DirectPipe cannot silently add latency to every probe.
    [SupportedOSPlatform("windows")]
    [Fact(Timeout = 10_000)]
    public async Task WaitReady_IsNoop()
    {
        string childPath = Path.Combine(AppContext.BaseDirectory, "Ghostty.Bench.EchoChild.exe");
        Assert.True(File.Exists(childPath), $"EchoChild not copied: {childPath}");

        using var transport = new DirectPipeTransport(childPath);

        // Pass a non-zero timeout AND assert wall-clock under a tight
        // ceiling. A zero-timeout passes any implementation that doesn't
        // poll-then-throw on first tick; a stopwatch ceiling catches
        // "added a Thread.Sleep" or "drained N bytes" regressions even
        // when the implementation respects the timeout.
        //
        // The stopwatch is started INSIDE the Task.Run lambda so we
        // measure WaitReady's wall-clock, not the thread-pool scheduling
        // latency of Task.Run itself (which can spike to 50ms+ when the
        // pool is saturated by parallel xunit test runs). 20ms is enough
        // headroom for cold-cache jitter while still being an order of
        // magnitude under any realistic non-no-op latency.
        // Wrapped in Task.Run to satisfy xunit 2.9's requirement that
        // [Fact(Timeout)] tests be async, matching ConPtyTransportTests.
        long elapsedMs = await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            transport.WaitReady(TimeSpan.FromMilliseconds(50));
            sw.Stop();
            return sw.ElapsedMilliseconds;
        });

        Assert.True(elapsedMs < 20,
            $"DirectPipeTransport.WaitReady should return instantly, took {elapsedMs}ms");
    }
}
