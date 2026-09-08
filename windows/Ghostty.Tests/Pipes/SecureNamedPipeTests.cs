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
    /// <summary>
    /// Deadline for the local handshake. Both halves are same-user, on this
    /// machine, against a server already parked in
    /// <c>WaitForConnectionAsync</c>: the kernel answers a CreateFileW on a
    /// listening pipe without waiting on anything, so a healthy handshake
    /// spends microseconds here. The number is a hang detector -- it turns
    /// "never" into a reported failure instead of a stuck run -- not a budget
    /// the handshake is expected to draw down.
    /// </summary>
    private const int HandshakeTimeoutMs = 10_000;

    private static string TestPipeName() =>
        "wintty-test-secure-pipe-" + Guid.NewGuid().ToString("N");

    // The kernel, not .NET, is the authority on what the DACL grants: this
    // asks the OS for the SDDL actually held on the handle. The default
    // pipe DACL grants Everyone (S-1-1-0) and Anonymous Logon; the factory
    // must produce neither.
    //
    // Nothing in here waits on anything: create the server, ask the kernel,
    // assert. That is deliberate. This assertion and the round trip that
    // proves the creating user is still granted access used to share one
    // method, so a connect that lost a race with the machine's load reported
    // the security claim as failed too -- it did on a loaded signoff run, at
    // 12s, against code whose DACL was correct. A security regression test
    // people have learned to read as "probably just the flake" is worse than
    // no test, so the half that cannot be slow no longer sits behind the half
    // that can.
    [Fact]
    public void CreateServer_DaclGrantsOnlyTheCreatingUser()
    {
        var name = TestPipeName();
        using var server = SecureNamedPipe.CreateServer(name);

        var sddl = PipeSecurityProbe.Sddl(server.SafePipeHandle);
        Assert.DoesNotContain("S-1-1-0", sddl, StringComparison.Ordinal); // Everyone
        Assert.DoesNotContain(";AU;", sddl, StringComparison.Ordinal); // Anonymous Logon
        Assert.DoesNotContain("(AU;", sddl, StringComparison.Ordinal);
    }

    // The other half of "only the creating user": the creating user must
    // still be granted access, or the pipe would be unusable rather than
    // merely locked down.
    [Fact]
    public void CreateServer_StillLetsTheCreatingUserConnect()
    {
        var name = TestPipeName();
        using var server = SecureNamedPipe.CreateServer(name);
        using var client = ConnectSameUser(server, name);
        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task ReadAtMostAsync_ReturnsShortPayloadExactly()
    {
        var name = TestPipeName();
        using var server = SecureNamedPipe.CreateServer(name);
        using var client = ConnectSameUser(server, name);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

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
        using var client = ConnectSameUser(server, name);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Two bytes past the cap: small enough to fit any pipe buffer (a
        // buffer-filling write would block until read), more than the
        // reader is allowed to accept.
        await client.WriteAsync(new byte[66], cts.Token);
        client.Close();

        var (text, overflow) = await SecureNamedPipe.ReadAtMostAsync(server, 64, cts.Token);
        Assert.True(overflow);
        Assert.Null(text);
    }

    /// <summary>
    /// Completes the same-user handshake against <paramref name="server"/>
    /// and hands back the connected client, with the server's accept already
    /// finished.
    /// </summary>
    /// <remarks>
    /// The client half is the BLOCKING <c>Connect</c>, run on the test's own
    /// thread, and that is the whole point of this helper.
    /// <c>NamedPipeClientStream.ConnectAsync</c> is not asynchronous
    /// underneath: it is <c>Task.Run</c> over the same blocking connect loop.
    /// Awaiting it therefore queues a fresh work item, and the connect does
    /// not begin until the pool schedules that item -- which on an
    /// oversubscribed machine (this suite runs its classes in parallel, and
    /// the signoff ladder runs beside builds) is bounded by nothing the test
    /// controls: a pool at its thread limit injects replacements at one or
    /// two a second. All of that waiting is charged to the test's deadline
    /// even though no pipe operation has started. The test body is already
    /// running on a thread, so blocking it begins the connect immediately and
    /// the deadline then covers only the handshake itself.
    /// </remarks>
    private static NamedPipeClientStream ConnectSameUser(
        NamedPipeServerStream server, string name)
    {
        var accept = server.WaitForConnectionAsync(CancellationToken.None);
        // If the connect below throws, the accept stays pending until the
        // server is disposed and then faults with nobody waiting on it.
        // Observe it here so it cannot resurface as an unobserved task
        // exception on the finalizer thread.
        accept.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        var client = new NamedPipeClientStream(
            ".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            client.Connect(HandshakeTimeoutMs);
            Assert.True(
                accept.Wait(HandshakeTimeoutMs),
                "client connected but the server's accept did not complete "
                + $"within {HandshakeTimeoutMs} ms");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
