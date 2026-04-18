namespace Ghostty.Bench.Transports;

public interface ITransport : IDisposable
{
    // Writes here reach the child process's stdin.
    Stream Input { get; }

    // Reads here receive the child process's stdout.
    Stream Output { get; }
}
