using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class PreviewTextFormatterTests
{
    [Fact]
    public void Drops_trailing_blank_lines()
    {
        var lines = PreviewTextFormatter.Format("a\nb\n   \n\n", maxRows: 10, maxCols: 80);
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Keeps_only_last_maxRows()
    {
        var lines = PreviewTextFormatter.Format("l1\nl2\nl3\nl4", maxRows: 2, maxCols: 80);
        Assert.Equal(new[] { "l3", "l4" }, lines);
    }

    [Fact]
    public void Clips_long_lines_to_maxCols()
    {
        var lines = PreviewTextFormatter.Format("abcdefgh", maxRows: 5, maxCols: 4);
        Assert.Equal(new[] { "abcd" }, lines);
    }

    [Fact]
    public void Strips_carriage_returns()
    {
        var lines = PreviewTextFormatter.Format("a\r\nb\r", maxRows: 5, maxCols: 80);
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Empty_or_all_blank_returns_empty()
    {
        Assert.Empty(PreviewTextFormatter.Format("", 5, 80));
        Assert.Empty(PreviewTextFormatter.Format("   \n\n  ", 5, 80));
        Assert.Empty(PreviewTextFormatter.Format(null, 5, 80));
    }

    [Fact]
    public void Fewer_lines_than_maxRows_keeps_all()
    {
        var lines = PreviewTextFormatter.Format("only", maxRows: 8, maxCols: 80);
        Assert.Equal(new[] { "only" }, lines);
    }

    [Fact]
    public void Strips_private_use_area_glyphs_and_trailing_space()
    {
        // U+E0B0 is the common powerline separator (private use area); it should
        // be dropped, and the trailing space it leaves trimmed. Constructed in
        // code so the source stays plain ASCII.
        var powerline = ((char)0xE0B0).ToString();
        var input = "pwsh" + powerline + " ";
        Assert.Equal(new[] { "pwsh" }, PreviewTextFormatter.Format(input, 5, 80));
    }

    [Fact]
    public void Strips_control_chars_within_a_line()
    {
        var lines = PreviewTextFormatter.Format("a\tbc", maxRows: 5, maxCols: 80);
        Assert.Equal(new[] { "abc" }, lines);
    }
}
