using System.Runtime.Versioning;
using Ghostty.Bench.Transports;
using Xunit;

namespace Ghostty.Tests.Bench;

public class ConPtyTransportTests
{
    // No byte-for-byte stdin assertion: conhost line-buffers input, VT-
    // translates keys, and renders output as a screen, so echo symmetry
    // is not the right contract for a ConPTY regression guard.
    // The bounds are hang guards and nothing more: on a loaded machine the
    // conhost spawn and the first scheduled read can both take seconds, and
    // a budget tight enough to measure that is one that flakes on a busy
    // box. 60s still fails fast against the marshalling regression this
    // test exists for, which produces no output at all.
    // The round-trip harness's per-iteration deadline is only exercisable
    // against real ConPTY: it is evaluated between synchronous reads, and
    // FakeTransport's scripted responder answers once per input READ, so
    // once the scripted burst is drained the next read blocks forever and
    // the deadline loop never re-runs. Real conhost keeps emitting VT
    // state bytes, so reads keep returning and the deadline can fire. The
    // throughput sibling is fake-covered (its deadline is a CTS cancelling
    // a blocked ReadAsync). If scripted mode ever grows a
    // streaming-responder variant, a fake-level round-trip deadline test
    // becomes possible and should be added then.
    [SupportedOSPlatform("windows")]
    [Fact(Timeout = 90_000)]
    public async Task Transport_DeliversConhostOutputWithinSeconds()
    {
        string childPath = Path.Combine(AppContext.BaseDirectory, "Ghostty.Bench.EchoChild.exe");
        Assert.True(File.Exists(childPath), $"EchoChild not copied: {childPath}");

        using var transport = new ConPtyTransport(childPath);

        // Read up to 256 bytes. Any n > 0 proves conhost is routing output.
        var readTask = Task.Run(() =>
        {
            var buf = new byte[256];
            return transport.Output.Read(buf, 0, buf.Length);
        });

        int bytesRead;
        try
        {
            bytesRead = await readTask.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            Assert.Fail("ConPtyTransport produced no output within 60s -- regression of UpdateProcThreadAttribute lpValue marshalling");
            throw; // unreachable: Assert.Fail throws, but satisfies definite-assignment.
        }

        Assert.True(bytesRead > 0, "ConPtyTransport output pipe EOF'd before conhost sent its VT preamble");
    }
}
