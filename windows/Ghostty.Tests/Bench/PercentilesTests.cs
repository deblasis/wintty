using Ghostty.Bench.Harness;
using Xunit;

namespace Ghostty.Tests.Bench;

public class PercentilesTests
{
    [Theory]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 50, 5L)]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 95, 10L)]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 99, 10L)]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 50, 5L)]
    [InlineData(new long[] { 42L }, 99, 42L)]
    [InlineData(new long[] { 1, 1, 1, 1, 1 }, 50, 1L)]
    public void Percentile_OnSortedArray_ReturnsExpectedValue(long[] sorted, int p, long expected)
    {
        Assert.Equal(expected, Percentiles.Of(sorted, p));
    }

    [Fact]
    public void Percentile_EmptyArray_Throws()
    {
        Assert.Throws<ArgumentException>(() => Percentiles.Of(Array.Empty<long>(), 50));
    }

    [Fact]
    public void Percentile_OutOfRange_Throws()
    {
        long[] data = [1, 2, 3];
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentiles.Of(data, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentiles.Of(data, 101));
    }
}
