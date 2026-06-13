using System;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabCycleSessionTests
{
    private static readonly string[] Three = { "active", "prev", "old" };

    [Fact]
    public void Forward_from_fresh_highlights_previous_active()
    {
        var s = new TabCycleSession<string>(Three);
        Assert.Equal("prev", s.Advance(forward: true));
        Assert.Equal("prev", s.Current);
    }

    [Fact]
    public void Reverse_from_fresh_highlights_last()
    {
        var s = new TabCycleSession<string>(Three);
        Assert.Equal("old", s.Advance(forward: false));
    }

    [Fact]
    public void Forward_advances_and_wraps_back_to_active()
    {
        var s = new TabCycleSession<string>(Three);
        Assert.Equal("prev", s.Advance(forward: true));
        Assert.Equal("old", s.Advance(forward: true));
        Assert.Equal("active", s.Advance(forward: true));
    }

    [Fact]
    public void Reverse_then_forward_returns_to_active()
    {
        var s = new TabCycleSession<string>(Three);
        Assert.Equal("old", s.Advance(forward: false));
        Assert.Equal("active", s.Advance(forward: true));
    }

    [Fact]
    public void Single_element_snapshot_always_returns_it()
    {
        var s = new TabCycleSession<string>(new[] { "only" });
        Assert.Equal("only", s.Current);
        Assert.Equal("only", s.Advance(forward: true));
        Assert.Equal("only", s.Advance(forward: false));
    }

    [Fact]
    public void Empty_snapshot_throws()
    {
        Assert.Throws<ArgumentException>(
            () => new TabCycleSession<string>(Array.Empty<string>()));
    }
}
