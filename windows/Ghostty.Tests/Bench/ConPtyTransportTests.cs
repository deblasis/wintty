using System.Runtime.Versioning;
using Ghostty.Bench.Transports;
using Xunit;

namespace Ghostty.Tests.Bench;

public class ConPtyTransportTests
{
    // No byte-for-byte stdin assertion: conhost line-buffers input, VT-
    // translates keys, and renders output as a screen, so echo symmetry
    // is not the right contract for a ConPTY regression guard.
    // 10s timeout: hang reintroduction must fail CI fast.
    [SupportedOSPlatform("windows")]
    [Fact(Timeout = 10_000)]
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
            bytesRead = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Assert.Fail("ConPtyTransport produced no output within 5s -- regression of UpdateProcThreadAttribute lpValue marshalling");
            throw; // unreachable: Assert.Fail throws, but satisfies definite-assignment.
        }

        Assert.True(bytesRead > 0, "ConPtyTransport output pipe EOF'd before conhost sent its VT preamble");
    }
}
