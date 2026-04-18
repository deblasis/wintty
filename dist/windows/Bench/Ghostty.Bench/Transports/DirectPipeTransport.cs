using System.Diagnostics;

namespace Ghostty.Bench.Transports;

// Spawns a child with stdin/stdout redirected via anonymous pipes.
// Under the hood .NET calls CreateProcess with STARTF_USESTDHANDLES
// and no PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE -- exactly the bypass
// shape that #112 will ship in production. This implementation is
// measurement-only and does not leave the bench project.
public sealed class DirectPipeTransport : ITransport
{
    private readonly Process _proc;
    private int _disposed;

    public DirectPipeTransport(string childExePath)
    {
        if (!File.Exists(childExePath))
        {
            throw new TransportException($"child binary not found: {childExePath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = childExePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            _proc = Process.Start(psi)
                ?? throw new TransportException("Process.Start returned null");
        }
        catch (Exception ex) when (ex is not TransportException)
        {
            throw new TransportException($"DirectPipeTransport spawn failed: {ex.Message}", ex);
        }
    }

    public Stream Input => _proc.StandardInput.BaseStream;
    public Stream Output => _proc.StandardOutput.BaseStream;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _proc.StandardInput.Close(); } catch { }
        try
        {
            if (!_proc.WaitForExit(5000))
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _proc.Dispose();
    }
}
