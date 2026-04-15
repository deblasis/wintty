using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ConfigTextTests
{
    [Fact]
    public void NormalizeLineEndings_LfOnly_Unchanged()
    {
        Assert.Equal("a\nb\nc", ConfigText.NormalizeLineEndings("a\nb\nc"));
    }

    [Fact]
    public void NormalizeLineEndings_Crlf_ConvertsToLf()
    {
        Assert.Equal("a\nb\nc\n", ConfigText.NormalizeLineEndings("a\r\nb\r\nc\r\n"));
    }

    [Fact]
    public void NormalizeLineEndings_BareCr_ConvertsToLf()
    {
        // WinUI 3's TextBox stores multi-line text with bare \r separators;
        // this is the shape that silently broke libghostty's parser.
        Assert.Equal("a\nb\nc", ConfigText.NormalizeLineEndings("a\rb\rc"));
    }

    [Fact]
    public void NormalizeLineEndings_Mixed_AllBecomeLf()
    {
        Assert.Equal("a\nb\nc\nd", ConfigText.NormalizeLineEndings("a\r\nb\rc\nd"));
    }

    [Fact]
    public void NormalizeLineEndings_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConfigText.NormalizeLineEndings(string.Empty));
    }

    [Fact]
    public void NormalizeLineEndings_NoLineBreaks_Unchanged()
    {
        Assert.Equal("no breaks here", ConfigText.NormalizeLineEndings("no breaks here"));
    }
}
