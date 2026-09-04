using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Pipes;
using Ghostty.Tests.Wiring;
using Xunit;

namespace Ghostty.Tests.Pipes;

public sealed class SecureNamedPipeTests
{
    private static string TestPipeName() =>
        "wintty-test-secure-pipe-" + Guid.NewGuid().ToString("N");

    // The kernel, not .NET, is the authority on what the DACL grants: this
    // asks the OS for the SDDL actually held on the handle. The default
    // pipe DACL grants Everyone (S-1-1-0) and Anonymous Logon; the factory
    // must produce neither.
    [Fact]
    public async Task CreateServer_DaclGrantsOnlyTheCreatingUser()
    {
        var name = TestPipeName();
        using var server = SecureNamedPipe.CreateServer(name);

        var sddl = PipeSecurityProbe.Sddl(server.SafePipeHandle);
        Assert.DoesNotContain("S-1-1-0", sddl, StringComparison.Ordinal); // Everyone
        Assert.DoesNotContain(";AU;", sddl, StringComparison.Ordinal); // Anonymous Logon
        Assert.DoesNotContain("(AU;", sddl, StringComparison.Ordinal);

        // The creating user must still be granted access, or the pipe would
        // be unusable rather than merely locked down: a same-user client
        // connects and completes a round trip.
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.WaitForConnectionAsync(connectCts.Token);
        using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000, connectCts.Token);
        await accept.WaitAsync(connectCts.Token);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task ReadAtMostAsync_ReturnsShortPayloadExactly()
    {
        var name = TestPipeName();
        using var server = SecureNamedPipe.CreateServer(name);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.WaitForConnectionAsync(cts.Token);
        using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000, cts.Token);
        await accept.WaitAsync(cts.Token);

        // Write, then close: the payload is buffered in the pipe and the
        // close is what gives the reader its end-of-stream. (No
        // WaitForPipeDrain here -- on the writer it blocks until the
        // READER has drained, which deadlocks before the read begins.)
        var payload = "V1\nshort payload";
        await client.WriteAsync(Encoding.UTF8.GetBytes(payload), cts.Token);
        client.Close();

        var (text, overflow) = await SecureNamedPipe.ReadAtMostAsync(server, 1024, cts.Token);
        Assert.False(overflow);
        Assert.Equal(payload, text);
    }

    [Fact]
    public async Task ReadAtMostAsync_FlagsOverflowPastTheCap()
    {
        var name = TestPipeName();
        using var server = SecureNamedPipe.CreateServer(name);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.WaitForConnectionAsync(cts.Token);
        using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000, cts.Token);
        await accept.WaitAsync(cts.Token);

        // Two bytes past the cap: small enough to fit any pipe buffer (a
        // buffer-filling write would block until read), more than the
        // reader is allowed to accept.
        await client.WriteAsync(new byte[66], cts.Token);
        client.Close();

        var (text, overflow) = await SecureNamedPipe.ReadAtMostAsync(server, 64, cts.Token);
        Assert.True(overflow);
        Assert.Null(text);
    }
}
