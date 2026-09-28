using Ghostty.Core.Gaps;
using Xunit;

namespace Ghostty.Tests.Gaps;

public class ReleaseVersionTests
{
    private static ReleaseVersion V(string s)
    {
        Assert.True(ReleaseVersion.TryParse(s, out var v), s);
        return v;
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0-rc.12")]
    [InlineData("1.0.0-rc.12", "1.0.0-rc.2")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.9")]
    public void Orders_by_semver_precedence(string higher, string lower)
    {
        Assert.True(V(higher).CompareTo(V(lower)) > 0);
        Assert.True(V(lower).CompareTo(V(higher)) < 0);
    }

    [Fact]
    public void Build_metadata_is_ignored()
    {
        Assert.Equal(0, V("1.0.0+abc").CompareTo(V("1.0.0")));
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_malformed(string? s)
    {
        Assert.False(ReleaseVersion.TryParse(s, out _));
    }

    [Fact]
    public void Core_drops_prerelease_and_build()
    {
        Assert.Equal("1.2.3", V("1.2.3-rc.1+alice").Core);
    }
}
