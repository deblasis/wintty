using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileHiddenKeyTests
{
    [Theory]
    [InlineData("pwsh", "profile.pwsh.hidden")]
    [InlineData("wsl-debian", "profile.wsl-debian.hidden")]
    [InlineData("a", "profile.a.hidden")]
    public void For_ReturnsDottedHiddenKey(string id, string expected)
    {
        Assert.Equal(expected, ProfileHiddenKey.For(id));
    }
}
