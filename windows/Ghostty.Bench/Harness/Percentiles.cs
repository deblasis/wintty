namespace Ghostty.Bench.Harness;

public static class Percentiles
{
    // Returns the p-th percentile of a sorted array using nearest-rank.
    // Caller is responsible for sorting the input; this class does not
    // sort defensively because the harness already sorts once and
    // passes the same array for p50/p95/p99.
    public static long Of(long[] sortedAscending, int percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Length == 0)
        {
            throw new ArgumentException("sortedAscending must not be empty", nameof(sortedAscending));
        }
        if (percentile < 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "must be in [0, 100]");
        }

        // Nearest-rank: index = ceil(p/100 * N) - 1, clamped to array bounds.
        int n = sortedAscending.Length;
        int index = (int)Math.Ceiling(percentile / 100.0 * n) - 1;
        if (index < 0) index = 0;
        if (index >= n) index = n - 1;
        return sortedAscending[index];
    }
}
