using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Pipes;
using Ghostty.Core.SingleInstance;
using Microsoft.Extensions.Logging;

namespace Ghostty.Hosting;

/// <summary>
/// The primary instance's named-pipe server for single-instance mode.
/// Accepts one forwarded <see cref="LaunchRequest"/> per client
/// connection and hands it to the <c>onLaunch</c> callback. The accept
/// loop is bounded by the shared <see cref="PipeServerRetryPolicy"/>
/// (the same policy the theme-preview pipe uses) so a server-creation
/// failure stands the loop down instead of busy-looping.
/// </summary>
public sealed partial class SingleInstanceServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Action<LaunchRequest> _onLaunch;
    private readonly ILogger<SingleInstanceServer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly PipeServerRetryPolicy _retryPolicy = new();
    private Task? _loop;

    public SingleInstanceServer(
        string pipeName,
        Action<LaunchRequest> onLaunch,
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
            // command line.
            server = SecureNamedPipe.CreateServer(_pipeName);
        }
        catch (IOException ex)
        {
            _logger.LogSingleInstancePipeUnavailable(ex);
            return PipeLoopOutcome.ServerCreationFailed;
        }

        try
        {
            using (server)
            {
                await server.WaitForConnectionAsync(ct);
                // The secondary writes Serialize() (which may itself
                // contain '\n' inside fields), so read the whole stream
                // -- but bounded: an unbounded ReadToEnd would buffer
                // whatever a connected peer cares to send.
                var (payload, overflow) = await SecureNamedPipe.ReadAtMostAsync(
                    server, LaunchRequest.MaxSerializedBytes, ct);
                if (overflow)
                {
                    _logger.LogSingleInstanceBadPayload();
                }
                else if (LaunchRequest.TryParse(payload, out var req) && req is not null)
                    _onLaunch(req);
                else
                    _logger.LogSingleInstanceBadPayload();
            }
            return PipeLoopOutcome.SessionEnded;
        }
        catch (OperationCanceledException)
        {
            return PipeLoopOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSingleInstancePipeError(ex);
            return PipeLoopOutcome.SessionFaulted;
        }
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
}

internal static partial class SingleInstanceServerLog
{
    [LoggerMessage(
        EventId = Ghostty.Logging.LogEvents.SingleInstance.PipeUnavailable,
        Level = LogLevel.Warning,
        Message = "Single-instance pipe server could not be created; standing down.")]
    internal static partial void LogSingleInstancePipeUnavailable(
        this ILogger<SingleInstanceServer> logger, Exception ex);

    [LoggerMessage(
        EventId = Ghostty.Logging.LogEvents.SingleInstance.PipeError,
        Level = LogLevel.Warning,
        Message = "Single-instance pipe session faulted.")]
    internal static partial void LogSingleInstancePipeError(
        this ILogger<SingleInstanceServer> logger, Exception ex);

    [LoggerMessage(
        EventId = Ghostty.Logging.LogEvents.SingleInstance.BadPayload,
        Level = LogLevel.Warning,
        Message = "Single-instance pipe received an unparseable launch payload; ignoring.")]
    internal static partial void LogSingleInstanceBadPayload(
        this ILogger<SingleInstanceServer> logger);
}
