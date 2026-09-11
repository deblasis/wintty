using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Pipes;
using Ghostty.Core.SingleInstance;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Windows.SingleInstance;

/// <summary>
/// The acknowledgement ordering of the primary's forwarding server (#1094):
/// the ACK is a voucher for a serviced launch, so it may not arrive before
/// the dispatch completes, and it may not arrive at all when the dispatch
/// faulted -- that is what turns a lost launch into a fallback window.
/// </summary>
public sealed class SingleInstanceServerTests
{
    private static string UniquePipeName()
        => "wintty-test-server-" + Guid.NewGuid().ToString("N");

    private static byte[] PayloadOf(LaunchRequest request)
        => Encoding.UTF8.GetBytes(request.Serialize());

    [Fact]
    public async Task AckArrivesOnlyAfterTheLaunchIsServed()
    {
        var pipe = UniquePipeName();
        var served = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new SingleInstanceServer(
            pipe,
            async _ =>
            {
                // Hold the dispatch open until the test says the window
                // "opened"; the ACK must not beat it out the door.
                await served.Task;
            },
            NullLogger<SingleInstanceServer>.Instance);
        server.Start();

        using var client = new NamedPipeClientStream(
            ".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(2000);
        var payload = PayloadOf(new LaunchRequest(@"C:\wd", new[] { "Wintty.exe" }));
        await client.WriteAsync(payload, 0, payload.Length);

        var buffer = new byte[1];
        // One read for the whole lifetime of the connection: a pipe stream
        // takes one pending read at a time, so the "no early ACK" half and
        // the "ACK after service" half must be the same read.
        var read = client.ReadAsync(buffer, 0, 1);
        Assert.False(
            read.Wait(TimeSpan.FromMilliseconds(300)),
            "an acknowledgement arrived before the launch was served");

        served.SetResult();
        Assert.True(
            read.Wait(TimeSpan.FromSeconds(5)),
            "the acknowledgement must follow the served launch");
        Assert.Equal(1, await read);
        Assert.Equal(LaunchRequest.Ack, buffer[0]);
    }

    [Fact]
    public async Task FaultedDispatch_SendsNoAck()
    {
        var pipe = UniquePipeName();
        using var server = new SingleInstanceServer(
            pipe,
            _ => Task.FromException(new InvalidOperationException("window refused to open")),
            NullLogger<SingleInstanceServer>.Instance);
        server.Start();

        using var client = new NamedPipeClientStream(
            ".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(2000);
        var payload = PayloadOf(new LaunchRequest(@"C:\wd", new[] { "Wintty.exe" }));
        await client.WriteAsync(payload, 0, payload.Length);

        var buffer = new byte[1];
        var read = client.ReadAsync(buffer, 0, 1);
        if (read.Wait(TimeSpan.FromSeconds(2)))
        {
            // Zero is the server hanging up on its side (the session faulted
            // and the connection went away): no acknowledgement either way.
            // One byte would have to be the acknowledgement to fail this.
            var received = await read;
            Assert.True(
                received == 0 || buffer[0] != LaunchRequest.Ack,
                "a faulted dispatch acknowledged a launch it did not serve");
        }
        // Timed out: nothing came back, which is the other no-ack shape.
    }

    /// <summary>
    /// The whole point of parse-as-you-go: the client keeps the connection
    /// open to receive the ACK, so the server must stop reading at the end
    /// of the (self-delimiting) payload rather than wait for an
    /// end-of-stream that is no longer coming.
    /// </summary>
    [Fact]
    public async Task ServerServesWhileTheClientHoldsTheConnectionOpen()
    {
        var pipe = UniquePipeName();
        var served = new TaskCompletionSource<LaunchRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new SingleInstanceServer(
            pipe,
            req =>
            {
                served.SetResult(req);
                return Task.CompletedTask;
            },
            NullLogger<SingleInstanceServer>.Instance);
        server.Start();

        using var client = new NamedPipeClientStream(
            ".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(2000);

        var request = new LaunchRequest(
            @"C:\wd",
            new[] { "Wintty.exe", "arg with spaces", "nül" });
        var payload = PayloadOf(request);
        await client.WriteAsync(payload, 0, payload.Length);
        // and then hold the connection open, no half-close, no dispose.

        var servedRequest = await AsyncHelpers.WithTimeout(served.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(request.WorkingDirectory, servedRequest.WorkingDirectory);
        Assert.Equal(request.Args, servedRequest.Args);

        var buffer = new byte[1];
        Assert.True(client.ReadAsync(buffer, 0, 1).Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(LaunchRequest.Ack, buffer[0]);
    }

    private static class AsyncHelpers
    {
        public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan budget)
        {
            var winner = await Task.WhenAny(task, Task.Delay(budget));
            if (winner != task)
                throw new TimeoutException($"did not complete within {budget}");
            return await task;
        }
    }
}
