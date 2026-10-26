using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ghostty.Bench.Transports;
using Xunit;

namespace Ghostty.Tests.Bench;

public class ConPtyTransportTests
{
    // The echo probe checks a short marker, not byte-for-byte terminal semantics:
    // conhost line-buffers input, VT-translates keys, and renders output as
    // a screen. It exists to prove the measured direction still works.
    // The bounds are hang guards and nothing more: on a loaded machine the
    // conhost spawn and the first scheduled read can both take seconds.
    // A dead child is detected by the process poll and exit code; the 15 s
    // budget is for a child that stays alive but never becomes ready.
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
    public async Task Transport_ReachesReadyAndEchoesBytes()
    {
        string childPath = Path.Combine(AppContext.BaseDirectory, "Ghostty.Bench.EchoChild.exe");
        Assert.True(File.Exists(childPath), $"EchoChild not copied: {childPath}");

        using var transport = new ConPtyTransport(childPath);

        // Conhost preamble bytes are not proof the child started. The child
        // emits RDY=111 only after all three standard handles are consoles;
        // a partial handle contract never writes it.
        await Task.Run(() => transport.WaitReady(TimeSpan.FromSeconds(15)))
            .WaitAsync(TimeSpan.FromSeconds(30));

        // The child's copy loop is the direction the benchmark measures.
        var echo = Task.Run(() =>
        {
            var buf = new byte[256];
            var window = new List<byte>();
            while (true)
            {
                int n = transport.Output.Read(buf, 0, buf.Length);
                if (n <= 0) return false;
                window.AddRange(buf.AsSpan(0, n));
                if (CollectionsMarshal.AsSpan(window).IndexOf("!~A"u8) >= 0) return true;
            }
        });

        ReadOnlySpan<byte> probe = "!~A"u8;
        transport.Input.Write(probe);
        transport.Input.Flush();
        bool echoed;
        try
        {
            echoed = await echo.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail("ConPty child did not echo a probe byte through the pty");
            return;
        }
        Assert.True(echoed, "ConPty child did not echo a probe byte through the pty");
    }
}
