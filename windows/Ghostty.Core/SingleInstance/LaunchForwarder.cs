using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// The secondary's half of single-instance forwarding: hand the launch to
/// the running primary over its named pipe and learn whether the primary
/// SERVED it. Served means the primary answered with
/// <see cref="LaunchRequest.Ack"/> after acting on the request, not merely
/// that the bytes were written, so a launch is never dropped on the floor
/// of a process that received it and hung.
/// </summary>
/// <remarks>
/// Failure is always an answer, never an exception: the caller's contract
/// is a boolean plus the reason (null when the reason is "the primary
/// never acknowledged within the budget"). On any failure the secondary
/// continues as an ordinary independent launch; it does not take the
/// mutex over, because the primary may merely be slow, and a second
/// pipe server on one name would double-serve every later forward. A
/// crashed primary needs no takeover at all: the OS releases the mutex
/// name with the process's last handle, and the next launch elects
/// itself primary again.
/// </remarks>
public static class LaunchForwarder
{
    /// <summary>
    /// How long to wait for the primary's acknowledgement. Generous on
    /// purpose: a healthy-but-busy primary (restoring a session under a
    /// forwarded click) can take seconds to open the window, and a false
    /// "unserved" turns one launch into two windows. A genuinely hung
    /// primary costs the caller this once; every later launch pays it
    /// too, and each still opens its own window.
    /// </summary>
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for the primary's pipe to answer a connect. Two
    /// seconds matches the pre-ACK behaviour: long enough for a primary
    /// mid-accept-loop, short enough that a wedged one is not felt as a
    /// hang.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Forward <paramref name="request"/> to the primary on
    /// <paramref name="pipeName"/>. Returns true only when the primary
    /// acknowledged serving the launch. On false, <paramref name="failure"/>
    /// carries the I/O failure when there was one, and stays null when the
    /// primary simply never acknowledged within
    /// <paramref name="ackTimeout"/> (the hung-primary case).
    /// </summary>
    public static bool TryForward(
        string pipeName,
        LaunchRequest request,
        out Exception? failure,
        TimeSpan? ackTimeout = null)
    {
        var budget = ackTimeout ?? DefaultAckTimeout;
        failure = null;
        try
        {
            // InOut for the acknowledgement; Asynchronous so every wait
            // below is a true overlapped one the dispose at the end can
            // abort; CurrentUserOnly for the same reason the server's
            // side carries it.
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);

            // Length-prefixed fields make the payload self-delimiting, so
            // quoting is not the secondary's problem: whatever the argv
            // contained round-trips byte for byte, and the working
            // directory travels as its own field. All waits are bounded by
            // the same budget: a primary that will not take the payload is
            // the same non-serving primary as one that will not answer.
            var payload = Encoding.UTF8.GetBytes(request.Serialize());
            if (!client.WriteAsync(payload, 0, payload.Length).Wait(budget))
            {
                failure = new TimeoutException(
                    "the primary did not accept the forwarded launch within the budget");
                return false;
            }

            return TryAwaitAck(client, budget, out failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
            return false;
        }
    }

    private static bool TryAwaitAck(
        NamedPipeClientStream client,
        TimeSpan budget,
        out Exception? failure)
    {
        failure = null;
        var buffer = new byte[1];
        var read = client.ReadAsync(buffer, 0, 1);

        if (!read.Wait(budget))
        {
            // The primary has the payload but has not served it: hung, or
            // (only inside an upgrade window) an older build that never
            // acknowledges. Append the cancel byte and give up on this
            // connection. Against the old primary the trailing byte makes
            // the payload unparseable, so it drops the launch rather than
            // opening a second window for one the fallback is about to
            // open itself; a current primary stopped reading at the end of
            // the payload and never sees the byte.
            try
            {
                var cancel = new[] { LaunchRequest.Cancel };
                client.WriteAsync(cancel, 0, cancel.Length)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }
            catch { /* the primary may already be gone; the launch is the fallback's now */ }

            // The read stays pending on a connection this caller is about
            // to dispose; observe its eventual fault so it is never an
            // unobserved one.
            _ = read.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
            failure = null;
            return false;
        }

        // A faulted read (primary died mid-acknowledgement) rethrows here
        // and lands in the caller's catch via TryForward's.
        return read.Result == 1 && buffer[0] == LaunchRequest.Ack;
    }
}
