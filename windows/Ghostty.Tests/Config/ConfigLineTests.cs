using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ConfigLineTests
{
    [Fact]
    public void ParseKeyValue()
    {
        var line = ConfigLine.Parse("font-size = 14");
        Assert.Equal("font-size", line.Key);
        Assert.Equal("14", line.Value);
        Assert.False(line.IsComment);
        Assert.False(line.IsEmpty);
    }

    [Fact]
    public void ParseComment()
    {
        var line = ConfigLine.Parse("# font-size = 14");
        Assert.True(line.IsComment);
        Assert.Null(line.Key);
    }

    [Fact]
    public void ParseEmpty()
    {
        var line = ConfigLine.Parse("");
        Assert.True(line.IsEmpty);
        Assert.Null(line.Key);
    }

    [Fact]
    public void ParseNoSpaces()
    {
        var line = ConfigLine.Parse("font-size=14");
        Assert.Equal("font-size", line.Key);
        Assert.Equal("14", line.Value);
    }

    [Fact]
    public void ParseValueWithEquals()
    {
        var line = ConfigLine.Parse("keybind = ctrl+shift+t=new_tab");
        Assert.Equal("keybind", line.Key);
        Assert.Equal("ctrl+shift+t=new_tab", line.Value);
    }

    [Fact]
    public void ParseValueWithTrailingHash()
    {
        // Ghostty does NOT support inline comments, so # is part of the value
        var line = ConfigLine.Parse("background = #1e1e2e");
        Assert.Equal("background", line.Key);
        Assert.Equal("#1e1e2e", line.Value);
    }
}
