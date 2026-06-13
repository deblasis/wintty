using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

public sealed class WindowsSingleInstanceKeyTests
{
    [Fact]
    public void Key_IsRegisteredAsWindowsOnly()
    {
        Assert.True(WindowsOnlyKeys.Contains("windows-single-instance"));
    }

    [Fact]
    public void Key_HasDescription()
    {
        Assert.True(WindowsOnlyKeys.ByKey.TryGetValue("windows-single-instance", out var entry));
        Assert.False(string.IsNullOrWhiteSpace(entry.Description));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("", false)]      // unset => default OFF
    [InlineData("1", false)]     // only canonical true/false honored
    public void ParseBool_MatchesGateSemantics(string raw, bool expected)
    {
        Assert.Equal(expected, WindowsOnlyKeyParsers.ParseBool(raw, defaultValue: false));
    }
}
