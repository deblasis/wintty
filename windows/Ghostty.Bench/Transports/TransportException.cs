namespace Ghostty.Bench.Transports;

public sealed class TransportException : Exception
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception inner) : base(message, inner) { }
}
