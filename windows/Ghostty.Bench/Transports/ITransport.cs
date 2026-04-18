namespace Ghostty.Bench.Transports;

public interface ITransport : IDisposable
{
    // Writes here reach the child process's stdin.
    Stream Input { get; }

    // Reads here receive the child process's stdout.
    Stream Output { get; }

    // Blocks until the child is ready to accept measurement traffic,
    // or throws on timeout. DirectPipe has nothing to wait for and
    // returns instantly. ConPty reads + discards conhost's VT preamble
    // up to and including the "RDY" sentinel emitted by EchoChild, so
    // subsequent round-trip reads start from a clean state.
    void WaitReady(TimeSpan timeout);
}
