using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class CachedValueTests
{
    [Fact]
    public void Get_WithinWindow_ReturnsCachedAndDoesNotRefetch()
    {
        long now = 0;
        var fetches = 0;
        var cv = new CachedValue<int>(durationMs: 500, fetch: () => ++fetches, nowMs: () => now);

        Assert.Equal(1, cv.Get());
        now = 499;
        Assert.Equal(1, cv.Get()); // still cached
        Assert.Equal(1, fetches);
    }

    [Fact]
    public void Get_AfterExpiry_Refetches()
    {
        long now = 0;
        var fetches = 0;
        var cv = new CachedValue<int>(durationMs: 500, fetch: () => ++fetches, nowMs: () => now);

        Assert.Equal(1, cv.Get());
        now = 500;
        Assert.Equal(2, cv.Get()); // expired exactly at the boundary
        Assert.Equal(2, fetches);
    }

    [Fact]
    public void Invalidate_ForcesRefetch()
    {
        long now = 0;
        var fetches = 0;
        var cv = new CachedValue<int>(durationMs: 500, fetch: () => ++fetches, nowMs: () => now);

        Assert.Equal(1, cv.Get());
        cv.Invalidate();
        Assert.Equal(2, cv.Get());
    }
}
