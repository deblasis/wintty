using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ConfigFileParserTests
{
    [Fact]
    public void SetValue_ExistingKey_ReplacesLastOccurrence()
    {
        var lines = new[] { "font-size = 12", "# a comment", "font-size = 14" };
        var result = ConfigFileParser.SetValue(lines, "font-size", "16");
        Assert.Equal("font-size = 12", result[0]);
        Assert.Equal("# a comment", result[1]);
        Assert.Equal("font-size = 16", result[2]);
    }

    [Fact]
    public void SetValue_NewKey_AppendsAtEnd()
    {
        var lines = new[] { "font-size = 14" };
        var result = ConfigFileParser.SetValue(lines, "background", "#000000");
        Assert.Equal(2, result.Length);
        Assert.Equal("font-size = 14", result[0]);
        Assert.Equal("background = #000000", result[1]);
    }

    [Fact]
    public void RemoveValue_CommentsOutAllOccurrences()
    {
        var lines = new[] { "font-size = 12", "background = #000000", "font-size = 14" };
        var result = ConfigFileParser.RemoveValue(lines, "font-size");
        Assert.Equal("# font-size = 12", result[0]);
        Assert.Equal("background = #000000", result[1]);
        Assert.Equal("# font-size = 14", result[2]);
    }

    [Fact]
    public void SetValue_SkipsCommentedOccurrences()
    {
        var lines = new[] { "# font-size = 12", "font-size = 14" };
        var result = ConfigFileParser.SetValue(lines, "font-size", "16");
        Assert.Equal("# font-size = 12", result[0]);
        Assert.Equal("font-size = 16", result[1]);
    }

    [Fact]
    public void SetKeybind_AppendsNewLine()
    {
        var lines = new[] { "keybind = ctrl+shift+t=new_tab" };
        var result = ConfigFileParser.SetKeybind(lines, "ctrl+shift+d=new_split:right");
        Assert.Equal(2, result.Length);
        Assert.Equal("keybind = ctrl+shift+t=new_tab", result[0]);
        Assert.Equal("keybind = ctrl+shift+d=new_split:right", result[1]);
    }

    [Fact]
    public void FindLastUncommented_ReturnsCorrectIndex()
    {
        var lines = new[] { "font-size = 12", "# font-size = 10", "font-size = 14", "background = #000000" };
        Assert.Equal(2, ConfigFileParser.FindLastUncommented(lines, "font-size"));
    }

    [Fact]
    public void FindLastUncommented_ReturnsNegativeOneWhenNotFound()
    {
        var lines = new[] { "# font-size = 12", "background = #000000" };
        Assert.Equal(-1, ConfigFileParser.FindLastUncommented(lines, "font-size"));
    }
}
