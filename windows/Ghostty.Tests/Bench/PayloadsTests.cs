using Ghostty.Bench.Probes;
using Xunit;

namespace Ghostty.Tests.Bench;

public class PayloadsTests
{
    private const int Expected = Payloads.TargetBytes;

    [Fact]
    public void Ascii_IsExactlyOneHundredMegabytes()
    {
        var p = Payloads.Ascii100Mb();
        Assert.Equal(Expected, p.Length);
    }

    [Fact]
    public void Ascii_ContainsOnlyUppercaseA()
    {
        var p = Payloads.Ascii100Mb();
        for (int i = 0; i < 1000; i++)
        {
            int idx = i * (p.Length / 1000);
            Assert.Equal((byte)'A', p.Span[idx]);
        }
    }

    [Fact]
    public void Sgr_IsExactlyOneHundredMegabytes()
    {
        var p = Payloads.Sgr100Mb();
        Assert.Equal(Expected, p.Length);
    }

    [Fact]
    public void Sgr_ContainsAtLeastHundredThousandRedSgrSequences()
    {
        var p = Payloads.Sgr100Mb().Span;
        int count = 0;
        ReadOnlySpan<byte> needle = [0x1b, (byte)'[', (byte)'3', (byte)'1', (byte)'m'];
        for (int i = 0; i <= p.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (p[i + j] != needle[j]) { match = false; break; }
            }
            if (match) { count++; i += needle.Length - 1; }
        }
        Assert.True(count >= 100_000, $"expected at least 100000 SGR red introducers, found {count}");
    }

    [Fact]
    public void Stress_IsExactlyOneHundredMegabytes()
    {
        var p = Payloads.Stress100Mb();
        Assert.Equal(Expected, p.Length);
    }

    [Fact]
    public void Stress_ContainsDcsOscAndApcIntroducers()
    {
        var p = Payloads.Stress100Mb().Span;
        // DCS = 0x1b 0x50, OSC = 0x1b 0x5d, APC = 0x1b 0x5f
        bool dcs = false, osc = false, apc = false;
        for (int i = 0; i < p.Length - 1; i++)
        {
            if (p[i] == 0x1b)
            {
                if (p[i + 1] == 0x50) dcs = true;
                else if (p[i + 1] == 0x5d) osc = true;
                else if (p[i + 1] == 0x5f) apc = true;
                if (dcs && osc && apc) break;
            }
        }
        Assert.True(dcs, "stress payload is missing DCS introducer");
        Assert.True(osc, "stress payload is missing OSC introducer");
        Assert.True(apc, "stress payload is missing APC introducer");
    }

    [Fact]
    public void Ascii_IsDeterministic()
    {
        var a = Payloads.Ascii100Mb();
        var b = Payloads.Ascii100Mb();
        Assert.Equal(a.Span.Slice(0, 1024).ToArray(), b.Span.Slice(0, 1024).ToArray());
    }
}
