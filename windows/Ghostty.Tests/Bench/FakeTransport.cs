using System.IO.Pipes;
using Ghostty.Bench.Transports;

namespace Ghostty.Tests.Bench;

// In-process ITransport that echoes every byte written to Input back
// on Output. Used by HarnessTests without spawning a real child.
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
    private readonly Thread _echoThread;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public FakeTransport()
    {
        string inputPipe  = $"fake-transport-in-{Guid.NewGuid():N}";
        string outputPipe = $"fake-transport-out-{Guid.NewGuid():N}";

        _inputServer = new NamedPipeServerStream(inputPipe,  PipeDirection.In,  maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte);
        _inputClient = new NamedPipeClientStream(".", inputPipe,  PipeDirection.Out);

        _outputServer = new NamedPipeServerStream(outputPipe, PipeDirection.Out, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte);
        _outputClient = new NamedPipeClientStream(".", outputPipe, PipeDirection.In);

        // Connect both pairs synchronously before starting the echo thread.
        // Order matters: server.WaitForConnection blocks until client connects.
        _inputClient.Connect();
        _inputServer.WaitForConnection();

        _outputClient.Connect();
        _outputServer.WaitForConnection();

        _echoThread = new Thread(EchoLoop) { IsBackground = true, Name = "FakeTransport.Echo" };
        _echoThread.Start();
    }

    // The harness writes to Input and reads from Output.
    // Input goes to _inputServer (server reads) -> echo -> _outputServer (server writes).
    // But we expose the client side so the harness has the writer for Input
    // and the reader for Output, matching ITransport semantics.
    public Stream Input  => _inputClient;   // harness writes here
    public Stream Output => _outputClient;  // harness reads here

    private void EchoLoop()
    {
        byte[] buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _inputServer.Read(buf, 0, buf.Length);
                if (n == 0) break;
                _outputServer.Write(buf, 0, n);
            }
        }
        catch (IOException) { /* harness closed its end */ }
        catch (ObjectDisposedException) { /* shutdown race */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        // Close the client write-end first so the echo thread's Read sees EOF.
        try { _inputClient.Dispose(); }  catch { }
        try { _inputServer.Dispose(); }  catch { }
        try { _outputServer.Dispose(); } catch { }
        try { _outputClient.Dispose(); } catch { }
        _echoThread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
