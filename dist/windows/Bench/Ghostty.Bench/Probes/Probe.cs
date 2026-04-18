using Ghostty.Bench.Output;
using Ghostty.Bench.Transports;

namespace Ghostty.Bench.Probes;

public abstract class Probe
{
    public abstract string Name { get; }
    public abstract ResultJson Run(ITransport transport, HostInfo host, DateTime timestampUtc);
}
