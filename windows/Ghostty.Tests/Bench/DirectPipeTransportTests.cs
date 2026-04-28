using System.Diagnostics;
using System.Runtime.Versioning;
using Ghostty.Bench.Transports;
using Xunit;

namespace Ghostty.Tests.Bench;

public class DirectPipeTransportTests
{
    // DirectPipe has no preamble to drain; WaitReady must be a no-op.
    [SupportedOSPlatform("windows")]
    [Fact(Timeout = 10_000)]
    public async Task WaitReady_IsNoop()
    {
        string childPath = Path.Combine(AppContext.BaseDirectory, "Ghostty.Bench.EchoChild.exe");
        Assert.True(File.Exists(childPath), $"EchoChild not copied: {childPath}");

        using var transport = new DirectPipeTransport(childPath);

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
