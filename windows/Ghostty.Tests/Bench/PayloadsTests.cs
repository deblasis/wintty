using Ghostty.Bench.Probes;
using Xunit;

namespace Ghostty.Tests.Bench;

public class PayloadsTests
{
    private const int Expected = Payloads.TargetBytes;

    [Fact]
    public void Ascii_IsExactlyOneMegabyte()
    {
        var p = Payloads.Ascii1Mb();
        Assert.Equal(Expected, p.Length);
    }

    [Fact]
    public void Ascii_ContainsOnlyUppercaseA()
    {
        var p = Payloads.Ascii1Mb();
        for (int i = 0; i < 1000; i++)
        {
            int idx = i * (p.Length / 1000);
            Assert.Equal((byte)'A', p.Span[idx]);
        }
    }

    [Fact]
    public void Sgr_IsExactlyOneMegabyte()
    {
        var p = Payloads.Sgr1Mb();
        Assert.Equal(Expected, p.Length);
    }

    [Fact]
    public void Sgr_ContainsAtLeastFiftyThousandRedSgrSequences()
    {
        // The SGR chunk is "\x1b[31mhello\x1b[0m " = 15 bytes. 1 MB / 15
        // = ~69 905 introducers; 50 000 is a comfortable floor that still
        // detects accidental factory changes (e.g., a payload switch that
        // dropped the SGR pattern entirely).
        var p = Payloads.Sgr1Mb().Span;
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
        Assert.True(count >= 50_000, $"expected at least 50000 SGR red introducers, found {count}");
    }

    [Fact]
    public void Stress_IsExactlyOneMegabyte()
    {
        var p = Payloads.Stress1Mb();
        Assert.Equal(Expected, p.Length);
    }

    [Fact]
    public void Stress_ContainsDcsOscAndApcIntroducers()
    {
        var p = Payloads.Stress1Mb().Span;
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
        var a = Payloads.Ascii1Mb();
        var b = Payloads.Ascii1Mb();
        Assert.Equal(a.Span.Slice(0, 1024).ToArray(), b.Span.Slice(0, 1024).ToArray());
    }

    // The throughput probe appends a "\r\n~ENDOFBURST_<nonce>~" terminator
    // after the payload and scans conhost's emitted VT stream for it. If a
    // payload factory ever emitted the literal "~ENDOFBURST_" substring,
    // the probe could false-match mid-payload and stop the measurement
    // early. This test pins the invariant so new payloads must preserve it.
    [Fact]
    public void Payloads_DoNotContainTerminatorPrefix()
    {
        byte[] marker = System.Text.Encoding.ASCII.GetBytes("~ENDOFBURST_");

        Assert.Equal(-1, Payloads.Ascii1Mb().Span.IndexOf(marker));
        Assert.Equal(-1, Payloads.Sgr1Mb().Span.IndexOf(marker));
        Assert.Equal(-1, Payloads.Stress1Mb().Span.IndexOf(marker));
    }
}
