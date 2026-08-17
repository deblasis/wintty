using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class LiveTitleGuardTests
{
    [Fact]
    public void Accepts_SameTerminal()
    {
        var term = new object();
        Assert.True(LiveTitleGuard.Accepts(term, term));
    }

    [Fact]
    public void Rejects_OtherTerminal()
    {
        Assert.False(LiveTitleGuard.Accepts(new object(), new object()));
    }

    [Fact]
    public void Rejects_NullActive()
    {
        Assert.False(LiveTitleGuard.Accepts(new object(), null));
        Assert.False(LiveTitleGuard.Accepts(null, new object()));
        Assert.False(LiveTitleGuard.Accepts(null, null));
    }
}
