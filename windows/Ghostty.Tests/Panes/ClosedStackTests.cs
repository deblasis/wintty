using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public class ClosedStackTests
{
    [Fact]
    public void Pop_returns_most_recently_pushed()
    {
        var s = new ClosedStack<int>(capacity: 5);
        s.Push(1);
        s.Push(2);
        s.Push(3);

        Assert.Equal(3, s.Count);
        Assert.True(s.TryPop(out var v));
        Assert.Equal(3, v);
        Assert.True(s.TryPop(out v));
        Assert.Equal(2, v);
    }

    [Fact]
    public void Empty_pop_returns_false()
    {
        var s = new ClosedStack<int>(capacity: 5);
        Assert.False(s.TryPop(out var v));
        Assert.Equal(0, v);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Pushing_past_capacity_evicts_oldest()
    {
        var s = new ClosedStack<int>(capacity: 3);
        s.Push(1); // oldest
        s.Push(2);
        s.Push(3);
        s.Push(4); // evicts 1

        Assert.Equal(3, s.Count);
        Assert.True(s.TryPop(out var v)); Assert.Equal(4, v);
        Assert.True(s.TryPop(out v)); Assert.Equal(3, v);
        Assert.True(s.TryPop(out v)); Assert.Equal(2, v);
        Assert.False(s.TryPop(out _)); // 1 was evicted
    }
}
