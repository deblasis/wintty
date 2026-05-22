using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class HoverLinkTextTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Format_NullOrEmpty_ReturnsEmpty(string? url, string expected)
    {
        Assert.Equal(expected, HoverLinkText.Format(url));
    }

    [Fact]
    public void Format_ShortUrl_PrependsPrefixWithoutTruncation()
    {
        var actual = HoverLinkText.Format("https://example.com");
        Assert.Equal("Ctrl+Click to open: https://example.com", actual);
    }

    [Fact]
    public void Format_LongUrl_TruncatesWithEllipsis()
    {
        var longUrl = "https://github.com/deblasis/wintty/pull/394/files#diff-1234567890abcdef1234567890abcdef";
        var actual = HoverLinkText.Format(longUrl, maxUrlChars: 40);

        Assert.StartsWith("Ctrl+Click to open: ", actual);
        var truncatedTail = actual["Ctrl+Click to open: ".Length..];
        Assert.Contains("…", truncatedTail);
        Assert.Equal(40, truncatedTail.Length);
        // Preserves scheme/host prefix.
        Assert.StartsWith("https://", truncatedTail);
        // Preserves resource tail.
        Assert.EndsWith("abcdef", truncatedTail);
    }

    [Theory]
    [InlineData("", 10, "")]
    [InlineData("abc", 10, "abc")]
    [InlineData("abcdefghij", 10, "abcdefghij")]
    public void TruncateMid_AtOrUnderLimit_ReturnsUnchanged(string input, int max, string expected)
    {
        Assert.Equal(expected, HoverLinkText.TruncateMid(input, max));
    }

    [Theory]
    [InlineData("abcdefghijk", 10, "abcd…ghijk")]
    [InlineData("abcdefghijkl", 11, "abcde…hijkl")]
    [InlineData("12345678901234567890", 9, "1234…7890")]
    public void TruncateMid_OverLimit_PreservesStartAndEndWithEllipsis(string input, int max, string expected)
    {
        var actual = HoverLinkText.TruncateMid(input, max);
        Assert.Equal(expected, actual);
        Assert.Equal(max, actual.Length);
    }

    [Theory]
    [InlineData("abcdef", 0, "")]
    [InlineData("abcdef", -1, "")]
    public void TruncateMid_NonPositiveMax_ReturnsEmpty(string input, int max, string expected)
    {
        Assert.Equal(expected, HoverLinkText.TruncateMid(input, max));
    }

    [Theory]
    [InlineData("abcdef", 1, "a")]
    [InlineData("abcdef", 2, "ab")]
    public void TruncateMid_TooSmallForEllipsis_HardTruncates(string input, int max, string expected)
    {
        // maxChars < 3 can't fit "x…y", so we just take a prefix.
        Assert.Equal(expected, HoverLinkText.TruncateMid(input, max));
    }
}
