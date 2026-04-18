using System.Runtime.Versioning;
using Ghostty.Bench.Transports;
using Xunit;

namespace Ghostty.Tests.Bench;

public class ConPtyTransportTests
{
    // Regression test for UpdateProcThreadAttribute lpValue marshalling.
    //
    // Before the audit fix, cmd.exe and a bare-C kernel32 child both
    // produced zero output through this transport -- conhost never
    // routed anything to the read-side pipe because the kernel stored
    // a pointer to a transient stack slot as the HPCON instead of the
    // HPCON value itself.
    //
    // After the fix, any ConPTY child triggers conhost to emit its VT
    // preamble (cursor-hide, focus-tracking mode sets, etc.) within a
    // few hundred milliseconds of spawn. Receiving any output at all is
    // sufficient to prove the marshalling is correct.
    //
    // This test deliberately does NOT assert on stdin-roundtrip content:
    // conhost sits between us and the child, applies line-buffering and
    // VT key translation on the input side, and VT screen rendering on
    // the output side, so byte-for-byte echo is not the right unit of
    // assertion for a ConPTY transport regression guard.
    //
    // Hard-timed to 10s so a reintroduction of the hang fails CI fast.
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

    // Note: WaitReady end-to-end validation is NOT a xunit integration test.
    // Under `dotnet test`'s testhost-spawned parent, the child's stdio
    // does not attach to the pseudo-console the same way as under a
    // `dotnet run` invocation from a native Windows console terminal, so
    // EchoChild's GetConsoleMode check returns false and the "RDY" sentinel
    // is never emitted. This is a context-specific property of the Windows
    // console subsystem, not a bug in WaitReady or EchoChild. End-to-end
    // coverage for the handshake lives in the manual validation step
    // documented in the PR: run `dotnet run --project dist/windows/Bench/
    // Ghostty.Bench -- conpty-roundtrip` from a real PowerShell terminal
    // (Windows Terminal or pwsh.exe) and confirm valid JSON output. Unit-
    // level coverage of the WaitReady drain logic lives in HarnessTests
    // via FakeTransport.
}
