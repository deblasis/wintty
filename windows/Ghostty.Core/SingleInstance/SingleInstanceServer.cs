using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Pipes;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// The primary instance's named-pipe server for single-instance mode.
/// Accepts one forwarded <see cref="LaunchRequest"/> per client connection,
/// awaits the <c>onLaunch</c> callback until the launch has been serviced,
/// and only then acknowledges the client: the acknowledgement is what lets
/// a secondary know its launch landed rather than trusting the write to a
/// process that may never act on it. A dispatch that faults sends no
/// acknowledgement, so the secondary falls back to launching itself
/// instead of exiting with nothing. The accept loop is bounded by the
/// shared <see cref="PipeServerRetryPolicy"/> (the same policy the
/// theme-preview pipe uses) so a server-creation failure stands the loop
/// down instead of busy-looping.
/// </summary>
/// <remarks>
/// Lives in Ghostty.Core, not the WinUI shell, for the same reason every
/// other type here does: the test hosts cannot reference the shell, and a
/// protocol whose acknowledgement ordering is its correctness ("after
/// service, never before") is exactly what a hand-written stub would drift
/// on. It holds no UI dependency; the shell hands it a callback.
/// </remarks>
public sealed class SingleInstanceServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<LaunchRequest, Task> _onLaunch;
    private readonly ILogger<SingleInstanceServer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly PipeServerRetryPolicy _retryPolicy = new();
    private Task? _loop;

    public SingleInstanceServer(
        string pipeName,
        Func<LaunchRequest, Task> onLaunch,
        ILogger<SingleInstanceServer> logger)
    {
        _pipeName = pipeName;
        _onLaunch = onLaunch;
        _logger = logger;
    }

    public void Start()
    {
        // Fire-and-forget: the loop owns its own cancellation and never
        // throws out (RunOneSession maps every fault to an outcome).
        _loop = Task.Run(() => RunServer(_cts.Token));
    }

    private async Task RunServer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome = await RunOneSession(ct);
            switch (_retryPolicy.Decide(outcome))
            {
                case PipeLoopDecision.Stop:
                case PipeLoopDecision.StandDown:
                    return;
                case PipeLoopDecision.RetryAfterBackoff:
                    try { await Task.Delay(_retryPolicy.Backoff, ct); }
                    catch (OperationCanceledException) { return; }
                    break;
                case PipeLoopDecision.RetryImmediately:
                default:
                    break;
            }
        }
    }

    private async Task<PipeLoopOutcome> RunOneSession(CancellationToken ct)
    {
        NamedPipeServerStream server;
        try
        {
            // SecureNamedPipe adds CurrentUserOnly: the name is
            // deterministic and publicly derivable, so the default pipe
            // DACL would let any process in the session hold the single
            // instance or post a launch the primary replays as its own
            // command line. InOut because the acknowledgement travels
            // back over this same connection.
            server = SecureNamedPipe.CreateServer(_pipeName, PipeDirection.InOut);
        }
        catch (IOException ex)
        {
            LogPipeUnavailable(ex);
            return PipeLoopOutcome.ServerCreationFailed;
        }

        try
        {
            using (server)
            {
                await server.WaitForConnectionAsync(ct);
                var request = await ReadRequestAsync(server, ct);
                if (request is null)
                {
                    LogBadPayload();
                }
                else
                {
                    // Awaiting the dispatch is the protocol: the
                    // acknowledgement may not precede the window it vouches
                    // for. A hung UI thread parks here with the connection
                    // open; the client's own timeout is what bounds that,
                    // and every later launch falls back to its own instance
                    // rather than waiting on this one.
                    await _onLaunch(request);
                    await server.WriteAsync(new[] { LaunchRequest.Ack }, ct);
                }
            }
            return PipeLoopOutcome.SessionEnded;
        }
        catch (OperationCanceledException)
        {
            return PipeLoopOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPipeError(ex);
            return PipeLoopOutcome.SessionFaulted;
        }
    }

    /// <summary>
    /// Read until the accumulated bytes parse as a complete
    /// <see cref="LaunchRequest"/>. The payload is self-delimiting (every
    /// field is length-prefixed and the parser rejects trailing bytes), so
    /// "it parses" is "it is whole": the client holds the connection open
    /// to receive the acknowledgement, so end-of-stream is no longer the
    /// end of the payload and must not be waited for. What arrives at
    /// end-of-stream instead is parsed once for the disconnecting-client
    /// case; more than the serialization cap is hostile input and reads as
    /// no payload at all.
    /// </summary>
    private static async Task<LaunchRequest?> ReadRequestAsync(
        NamedPipeServerStream server,
        CancellationToken ct)
    {
        var buffer = new byte[LaunchRequest.MaxSerializedBytes + 1];
        var total = 0;
        while (total <= LaunchRequest.MaxSerializedBytes)
        {
            var read = await server.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                // Client disconnected: parse exactly what arrived, once.
                var received = Encoding.UTF8.GetString(buffer, 0, total);
                return LaunchRequest.TryParse(received, out var tail)
                    ? tail
                    : null;
            }
            total += read;
            var candidate = Encoding.UTF8.GetString(buffer, 0, total);
            if (LaunchRequest.TryParse(candidate, out var request)
                && request is not null)
                return request;
        }

        // More than the serialization cap: hostile input, not a launch.
        // The old reader dropped overflow on the same line; a prefix that
        // happens to parse is not a reason to start honoring it.
        return null;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        // Best-effort: nudge a blocked WaitForConnectionAsync awake by
        // connecting a throwaway client, so the loop observes cancellation
        // promptly rather than at the next OS timeout.
        try
        {
            using var nudge = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            nudge.Connect(50);
        }
        catch { /* server may already be gone */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _cts.Dispose();
    }

    private void LogPipeUnavailable(Exception ex) =>
        _logger.LogPipeUnavailable(ex);

    private void LogPipeError(Exception ex) =>
        _logger.LogPipeError(ex);

    private void LogBadPayload() =>
        _logger.LogBadPayload();
}

internal static partial class SingleInstanceServerLog
{
    [LoggerMessage(
        EventId = Ghostty.Core.Logging.LogEvents.SingleInstance.PipeUnavailable,
        Level = LogLevel.Warning,
        Message = "Single-instance pipe server could not be created; standing down.")]
    internal static partial void LogPipeUnavailable(
        this ILogger<SingleInstanceServer> logger, Exception ex);

    [LoggerMessage(
        EventId = Ghostty.Core.Logging.LogEvents.SingleInstance.PipeError,
        Level = LogLevel.Warning,
        Message = "Single-instance pipe session faulted.")]
    internal static partial void LogPipeError(
        this ILogger<SingleInstanceServer> logger, Exception ex);

    [LoggerMessage(
        EventId = Ghostty.Core.Logging.LogEvents.SingleInstance.BadPayload,
        Level = LogLevel.Warning,
        Message = "Single-instance pipe received an unparseable launch payload; ignoring.")]
    internal static partial void LogBadPayload(
        this ILogger<SingleInstanceServer> logger);
}
