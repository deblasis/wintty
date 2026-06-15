using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class MruListTests
{
    [Fact]
    public void Touch_new_item_inserts_at_front()
    {
        var mru = new MruList<string>();
        mru.Touch("a");
        mru.Touch("b");
        Assert.Equal(new[] { "b", "a" }, mru.Order);
    }

    [Fact]
    public void Touch_existing_item_moves_it_to_front()
    {
        var mru = new MruList<string>();
        mru.Touch("a");
        mru.Touch("b");
        mru.Touch("c");
        mru.Touch("a");
        Assert.Equal(new[] { "a", "c", "b" }, mru.Order);
    }

    [Fact]
    public void Remove_drops_the_item()
    {
        var mru = new MruList<string>();
        mru.Touch("a");
        mru.Touch("b");
        mru.Remove("a");
        Assert.Equal(new[] { "b" }, mru.Order);
    }

    [Fact]
    public void Remove_missing_item_is_noop()
    {
        var mru = new MruList<string>();
        mru.Touch("a");
        mru.Remove("z");
        Assert.Equal(new[] { "a" }, mru.Order);
    }

    [Fact]
    public void Remove_then_touch_reinserts_at_front()
    {
        var mru = new MruList<string>();
        mru.Touch("a");
        mru.Touch("b");
        mru.Remove("a");
        mru.Touch("a");
        Assert.Equal(new[] { "a", "b" }, mru.Order);
    }
}
