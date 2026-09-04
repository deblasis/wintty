using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Pipes;

/// <summary>
/// Creates the named-pipe servers Ghostty hosts, with the ACL the IPC
/// contract requires: only the creating user may connect.
///
/// <para>
/// The pipe names are deterministic and publicly derivable, so the default
/// named-pipe DACL -- which grants Everyone and Anonymous Logon generic read
/// and write on modern Windows -- is an open door: any process in the session
/// can hold the single server instance (denial of service on launch
/// forwarding) and post payloads the primary replays as its own command
/// line. <see cref="PipeOptions.CurrentUserOnly"/> is the ACL: it restricts
/// the DACL to the creating user regardless of what the OS default is this
/// release. Both long-lived servers (single-instance launch forwarding and
/// theme preview) must come from here so the guarantee cannot drift between
/// them.
/// </para>
/// </summary>
public static class SecureNamedPipe
{
    /// <summary>
    /// Create a single-instance, inbound named-pipe server restricted to
    /// the creating user. Throws <see cref="IOException"/> when the name is
    /// already held (the caller's retry policy decides stand-down).
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName) =>
        new(
            pipeName,
            PipeDirection.In,
            1, // single instance: one session at a time
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly,
            64 * 1024, // in/out buffer: with the zero default, every write
            64 * 1024); // blocks until a reader consumes concurrently

    /// <summary>
    /// Read the whole pipe as UTF-8, but never more than
    /// <paramref name="maxBytes"/> bytes. Returns null payload and
    /// <c>Overflow = true</c> when the peer sent more than the cap, so a
    /// hostile client cannot make the reader buffer an unbounded stream.
    /// </summary>
    public static async Task<(string? Payload, bool Overflow)> ReadAtMostAsync(
        PipeStream pipe,
        int maxBytes,
        CancellationToken ct)
    {
        // One byte past the cap is read on purpose: it is what turns "the
        // peer sent exactly the cap" (fine) into "the peer sent more"
        // (overflow) without buffering anything past it.
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total <= maxBytes)
        {
            var read = await pipe.ReadAsync(
                buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break; // end of stream
            total += read;
        }

        if (total > maxBytes)
            return (null, true);
        return (Encoding.UTF8.GetString(buffer, 0, total), false);
    }
}
