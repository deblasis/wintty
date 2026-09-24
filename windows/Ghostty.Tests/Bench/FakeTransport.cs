using System.IO.Pipes;
using Ghostty.Bench.Transports;

namespace Ghostty.Tests.Bench;

public enum FakeTransportMode
{
    Echo,
    Scripted,
}

// Uses two named pipes with unique GUIDs for clean in-process semantics:
// anonymous pipes' DisposeLocalCopyOfClientHandle is only safe after a
// real fork, so in-process it would close the only reader/writer.
public sealed class FakeTransport : ITransport
{
    private readonly NamedPipeServerStream _inputServer;
    private readonly NamedPipeClientStream _inputClient;
    private readonly NamedPipeServerStream _outputServer;
    private readonly NamedPipeClientStream _outputClient;
    private readonly Thread _ioThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly FakeTransportMode _mode;
    private readonly Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?>? _scriptedResponder;
    private int _disposed;

    public int DisposeCount => Volatile.Read(ref _disposed);

    public FakeTransport() : this(FakeTransportMode.Echo, scriptedResponder: null) { }

    public FakeTransport(Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?> scriptedResponder)
        : this(FakeTransportMode.Scripted, scriptedResponder) { }

    private FakeTransport(FakeTransportMode mode, Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?>? scriptedResponder)
    {
        if (mode == FakeTransportMode.Scripted)
        {
            ArgumentNullException.ThrowIfNull(scriptedResponder);
        }
        _mode = mode;
        _scriptedResponder = scriptedResponder;

        string inputPipe  = $"fake-transport-in-{Guid.NewGuid():N}";
        string outputPipe = $"fake-transport-out-{Guid.NewGuid():N}";

        // Explicit nonzero kernel buffer sizes. The default constructors pass
        // 0, which on Windows named pipes means no store-and-forward: a
        // WriteFile only completes against an outstanding reader. That
        // deadlocked the bench tests whenever the harness reader cancelled
        // before its first read: the IO thread blocked forever in its first
        // scripted response write and the test thread blocked forever
        // writing payload. 64 KB comfortably holds every response and payload
        // these tests use (max 4 KB), so writes complete immediately.
        //
        // The OUTPUT pair is opened Asynchronous on BOTH ends, and the two
        // ends must agree. Without FILE_FLAG_OVERLAPPED the harness's
        // PipeStream.ReadAsync is emulated: the blocking read is queued to
        // the thread pool, so every read depends on a worker being free and
        // a loaded suite lets the caller's deadline fire while the read is
        // still queued (issue #917: a TimeoutException that reported a
        // property of the fake as a claim about the peer). The ends must
        // MATCH: an async client against a synchronous server made
        // RunThroughputIteration_IgnoresWrongNonceMatch and two others fail
        // deterministically, so both flip together.
        // The INPUT pair stays synchronous: its write side is Runner's sync
        // Input.Write into a 64 KB kernel buffer, which completes
        // immediately, and its read side is this class's own dedicated IO
        // thread, which no deadline is measured against.
        const int PipeBufferSize = 64 * 1024;
        _inputServer = new NamedPipeServerStream(inputPipe,  PipeDirection.In,  maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.None, PipeBufferSize, PipeBufferSize);
        _inputClient = new NamedPipeClientStream(".", inputPipe,  PipeDirection.Out);

        _outputServer = new NamedPipeServerStream(outputPipe, PipeDirection.Out, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferSize, PipeBufferSize);
        _outputClient = new NamedPipeClientStream(".", outputPipe, PipeDirection.In, PipeOptions.Asynchronous);

        // Connect both pairs synchronously before starting the IO thread.
        // Order matters: server.WaitForConnection blocks until client connects.
        _inputClient.Connect();
        _inputServer.WaitForConnection();

        _outputClient.Connect();
        _outputServer.WaitForConnection();

        _ioThread = new Thread(IoLoop) { IsBackground = true, Name = "FakeTransport.IO" };
        _ioThread.Start();
    }

    public Stream Input  => _inputClient;   // harness writes here
    public Stream Output => _outputClient;  // harness reads here

    /// <summary>
    /// Close the output after the next scripted response, in the same turn
    /// that writes it.
    /// </summary>
    /// <remarks>
    /// The responder is called once per input READ, so a test that wanted
    /// "some data, then EOF" had to script it across two calls and therefore
    /// assumed the harness's two Writes arrive as two Reads. They do not
    /// reliably: a descheduled IO thread lets both Writes accumulate and one
    /// Read drains them together, the second call never comes, and the test
    /// times out instead of seeing the EOF it was about. Closing in the same
    /// turn as the response makes the sequence a fact rather than a race.
    /// </remarks>
    public void CloseOutputAfterNextResponse() => Volatile.Write(ref _closeAfterResponse, 1);

    private int _closeAfterResponse;

    // No-op: the in-process fake has no preamble. Scripted and echo modes
    // both skip any drain step; the caller is responsible for shaping the
    // pipe's initial state via the scripted responder if needed.
    public void WaitReady(TimeSpan timeout) { }

    private void IoLoop()
    {
        byte[] buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _inputServer.Read(buf, 0, buf.Length);
                if (n == 0) break;

                if (_mode == FakeTransportMode.Echo)
                {
                    _outputServer.Write(buf, 0, n);
                }
                else
                {
                    var response = _scriptedResponder!(new ReadOnlyMemory<byte>(buf, 0, n));
                    if (response is null)
                    {
                        // Null return means "close output to emulate EOF" for the
                        // EndOfStreamException test path. Explicitly dispose the
                        // output server so the harness's Output.Read returns 0
                        // instead of blocking; just breaking the loop wouldn't
                        // signal EOF to the client end.
                        try { _outputServer.Dispose(); } catch { }
                        break;
                    }
                    _outputServer.Write(response.Value.Span);
                    if (Volatile.Read(ref _closeAfterResponse) == 1)
                    {
                        _outputServer.Flush();
                        try { _outputServer.Dispose(); } catch { }
                        break;
                    }
                }
            }
        }
        catch (IOException) { /* harness closed its end */ }
        catch (ObjectDisposedException) { /* shutdown race */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        // Close the client write-end first so the IO thread's Read sees EOF.
        try { _inputClient.Dispose(); }  catch { }
        try { _inputServer.Dispose(); }  catch { }
        try { _outputServer.Dispose(); } catch { }
        try { _outputClient.Dispose(); } catch { }
        _ioThread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
