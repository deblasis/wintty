using System.IO.Pipes;
using Ghostty.Bench.Transports;

namespace Ghostty.Tests.Bench;

public enum FakeTransportMode
{
    // Existing behavior: every byte written to Input is copied back on
    // Output. Used by HarnessTests that exercise Harness.RunRoundTrip
    // against a trivial loopback so byte-for-byte symmetry holds.
    Echo,

    // Scripted: each input write from the harness causes the scripted
    // emitter to enqueue a caller-supplied response sequence. Used by
    // HarnessTests that need to assert sentinel-scan behavior against
    // VT-like noise patterns.
    Scripted,
}

// In-process ITransport used by HarnessTests. Defaults to echo mode for
// backward compat with the existing tests. Scripted mode lets callers
// simulate conhost's "VT noise plus sentinel" output shape without
// spawning a real child.
//
// Uses two named pipes with unique GUIDs so we get clean in-process
// semantics without the handle-ownership complications of anonymous
// pipes (DisposeLocalCopyOfClientHandle is only safe after a real
// fork; in-process it closes the only reader/writer and breaks IO).
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

    // Echo-mode constructor. Preserves the original behavior: every write
    // to Input is copied verbatim to Output. Existing HarnessTests rely
    // on this; don't change its semantics.
    public FakeTransport() : this(FakeTransportMode.Echo, scriptedResponder: null) { }

    // Scripted-mode constructor. `scriptedResponder` is called once per
    // input write chunk read from the pipe. If it returns a byte buffer,
    // that buffer is written to Output. If it returns null, the Output
    // stream is closed (emulating child death / EOF). This is the hook
    // HarnessTests uses to feed SentinelRoundTrip hand-crafted VT noise
    // plus sentinel bytes.
    public FakeTransport(Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?> scriptedResponder)
        : this(FakeTransportMode.Scripted, scriptedResponder) { }

    private FakeTransport(FakeTransportMode mode, Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?>? scriptedResponder)
    {
        if (mode == FakeTransportMode.Scripted)
        {
            // Defense-in-depth: the public scripted ctor already requires a
            // non-null responder at the call site, but validate again here so
            // the IoLoop can call the responder without a null-forgiving operator.
            ArgumentNullException.ThrowIfNull(scriptedResponder);
        }
        _mode = mode;
        _scriptedResponder = scriptedResponder;

        string inputPipe  = $"fake-transport-in-{Guid.NewGuid():N}";
        string outputPipe = $"fake-transport-out-{Guid.NewGuid():N}";

        _inputServer = new NamedPipeServerStream(inputPipe,  PipeDirection.In,  maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte);
        _inputClient = new NamedPipeClientStream(".", inputPipe,  PipeDirection.Out);

        _outputServer = new NamedPipeServerStream(outputPipe, PipeDirection.Out, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte);
        _outputClient = new NamedPipeClientStream(".", outputPipe, PipeDirection.In);

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
