using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public sealed class HighContrastKeyTests
{
    [Fact]
    public void Registered_AsWindowsOnlyKey()
    {
        Assert.True(WindowsOnlyKeys.Contains("windows-high-contrast"));
    }

    [Theory]
    [InlineData("", true)]        // unset -> default on (auto-follow OS)
    [InlineData("true", true)]    // explicit on
    [InlineData("false", false)]  // opt out
    [InlineData("garbage", true)] // unparseable -> default
    public void OptOut_ParsesAsBoolDefaultTrue(string raw, bool expected)
    {
        Assert.Equal(expected, WindowsOnlyKeyParsers.ParseBool(raw, defaultValue: true));
    }
}
