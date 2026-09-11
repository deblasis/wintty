using System;
using System.Diagnostics;
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
/// The forwarding half of single-instance (#1094): a secondary must learn
/// whether the primary actually SERVED its launch, and a primary that never
/// answers must cost one bounded timeout, not the launch. These run the
/// real production types on both ends of a real pipe -- the server is a
/// Ghostty.Core type precisely so a test host can hold it.
/// </summary>
public sealed class LaunchForwarderTests
{
    private static string UniquePipeName()
        => "wintty-test-forward-" + Guid.NewGuid().ToString("N");

    private static LaunchRequest SampleRequest() => new(
        @"C:\some\working dir",
        new[] { "Wintty.exe", "--jumplist-action=new-tab", "spaced arg", "ünicode" });

    [Fact]
    public async Task ServedPrimary_ForwardReturnsTrueAndRequestArrivesIntact()
    {
        var pipe = UniquePipeName();
        var received = new TaskCompletionSource<LaunchRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new SingleInstanceServer(
            pipe,
            async req =>
            {
                received.SetResult(req);
                await Task.Yield();
            },
            NullLogger<SingleInstanceServer>.Instance);
        server.Start();

        var forwarded = Task.Run(() =>
            LaunchForwarder.TryForward(pipe, SampleRequest(), out _));

        var request = await AsyncHelpers.WithTimeout(received.Task, TimeSpan.FromSeconds(5));
        Assert.True(
            await forwarded,
            "the primary acknowledged, so the forward must report served");
        Assert.Equal(@"C:\some\working dir", request.WorkingDirectory);
        Assert.Equal(
            new[] { "Wintty.exe", "--jumplist-action=new-tab", "spaced arg", "ünicode" },
            request.Args);
    }

    /// <summary>
    /// THE hung-primary pin (#1094): a primary whose dispatch never
    /// completes (a wedged UI thread is the real-world shape) must not
    /// strand the secondary. The forward returns false after the
    /// acknowledgement timeout with no I/O failure, and the caller turns
    /// that into its own independent launch.
    /// </summary>
    [Fact]
    public async Task HungPrimary_FallsBackAfterTheAckTimeout()
    {
        var pipe = UniquePipeName();
        var sawRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new SingleInstanceServer(
            pipe,
            async req =>
            {
                sawRequest.SetResult();
                await release.Task; // never completes until the test ends
            },
            NullLogger<SingleInstanceServer>.Instance);
        server.Start();

        var ackBudget = TimeSpan.FromMilliseconds(400);
        var stopwatch = Stopwatch.StartNew();
        var forwarded = LaunchForwarder.TryForward(
            pipe, SampleRequest(), out var failure, ackTimeout: ackBudget);
        stopwatch.Stop();

        Assert.False(forwarded, "a never-acknowledging primary must not count as served");
        Assert.Null(failure); // a timeout, not an I/O failure
        Assert.True(
            stopwatch.Elapsed >= ackBudget,
            $"the fallback must wait the full budget (waited {stopwatch.Elapsed})");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"the fallback must be bounded by the budget (waited {stopwatch.Elapsed})");
        Assert.True(
            sawRequest.Task.IsCompleted,
            "the primary received the payload while never serving it");

        release.SetResult();
        await sawRequest.Task;
    }

    [Fact]
    public void AbsentPrimary_ConnectFailsAndReportsFailure()
    {
        // Short injected connect budget: the production default (5s of
        // retries) is exercised by the cold-start test below.
        var forwarded = LaunchForwarder.TryForward(
            UniquePipeName(), SampleRequest(), out var failure,
            connectBudget: TimeSpan.FromMilliseconds(600));

        Assert.False(forwarded);
        Assert.NotNull(failure); // nobody home is an I/O failure, not a timeout
        Assert.IsType<TimeoutException>(failure);
    }

    /// <summary>
    /// #1094 review L2: the primary takes its mutex long before its pipe
    /// server exists (the server starts deep in OnLaunched, after config,
    /// profiles and jump-list work), so a second launch inside that window
    /// used to burn its one 2-second connect attempt and fall back to a
    /// standalone process. The forwarder must keep retrying within a small
    /// total budget and reach a primary that comes up late.
    /// </summary>
    [Fact]
    public async Task ColdStartPrimary_ComingUpAfterTwoSeconds_IsStillForwardedTo()
    {
        var pipe = UniquePipeName();
        var request = SampleRequest();

        // A primary whose pipe server appears 3 seconds from now: past the
        // old single 2-second attempt, inside the retry budget.
        var server = new SingleInstanceServer(
            pipe,
            _ => Task.CompletedTask,
            NullLogger<SingleInstanceServer>.Instance);
        var lateStart = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            server.Start();
        });

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var forwarded = LaunchForwarder.TryForward(pipe, request, out var failure);
            stopwatch.Stop();

            Assert.True(
                forwarded,
                "a primary coming up inside the connect budget must still be forwarded to");
            Assert.Null(failure);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"the connect budget must stay bounded (took {stopwatch.Elapsed})");
        }
        finally
        {
            await lateStart;
            server.Dispose();
        }
    }

    /// <summary>
    /// The upgrade window: a pre-ACK primary (an older build still running
    /// after an update was installed) reads to end-of-stream and never
    /// acknowledges. The secondary's trailing cancel byte must make the
    /// payload unparseable for THAT reader, so it drops the launch instead
    /// of opening a second window on top of the secondary's own fallback.
    /// </summary>
    [Fact]
    public async Task TimedOutForward_CancelsThePayloadForAPreAckPrimary()
    {
        var pipe = UniquePipeName();
        var received = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // An old-primary impersonation: accept, read until the client
        // disconnects, never answer.
        var reader = Task.Run(async () =>
        {
            using var server = SecureNamedPipe.CreateServer(pipe, PipeDirection.InOut);
            await server.WaitForConnectionAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await server.ReadAsync(buffer);
                if (read == 0) break;
                memory.Write(buffer, 0, read);
            }
            received.SetResult(memory.ToArray());
        });

        var request = SampleRequest();
        var ackBudget = TimeSpan.FromMilliseconds(300);
        Assert.False(
            LaunchForwarder.TryForward(pipe, request, out _, ackTimeout: ackBudget),
            "the old primary never acknowledges");

        var bytes = await AsyncHelpers.WithTimeout(received.Task, TimeSpan.FromSeconds(5));
        var payloadOnly = bytes[..^1];
        var withCancel = bytes;

        Assert.Equal(LaunchRequest.Cancel, bytes[^1]);
        Assert.True(
            LaunchRequest.TryParse(Encoding.UTF8.GetString(payloadOnly), out var parsed)
                && parsed is not null,
            "the payload itself must still be the honest request");
        Assert.False(
            LaunchRequest.TryParse(Encoding.UTF8.GetString(withCancel), out _),
            "payload plus cancel byte must be unparseable for the old primary");

        await reader;
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
