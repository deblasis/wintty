using System.Diagnostics;
using Ghostty.Bench.Harness;
using Xunit;

namespace Ghostty.Tests.Bench;

public class HarnessTests
{
    [Fact]
    public void RunRoundTrip_RunsExactIterationCount()
    {
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 10, samples: 50);
        Assert.Equal(50, timings.Length);
    }

    [Fact]
    public void RunRoundTrip_WarmupIsExcludedFromTimings()
    {
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 10, samples: 50);
        Assert.Equal(50, timings.Length);
        // Cannot assert on absolute times because this is a loopback fake,
        // but we can assert the count is samples only, not warmup+samples.
    }

    [Fact]
    public void RunRoundTrip_AllTimingsAreNonNegative()
    {
        using var t = new FakeTransport();
        long[] timings = Runner.RunRoundTrip(t, warmup: 5, samples: 20);
        foreach (var ticks in timings)
        {
            Assert.True(ticks >= 0, $"timing was negative: {ticks}");
        }
    }

    [Fact]
    public void RunRoundTrip_ThrowsOnZeroSamples()
    {
        using var t = new FakeTransport();
        Assert.Throws<ArgumentOutOfRangeException>(() => Runner.RunRoundTrip(t, warmup: 10, samples: 0));
    }
}
