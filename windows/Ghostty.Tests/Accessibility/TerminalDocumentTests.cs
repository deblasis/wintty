using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class TerminalDocumentTests
{
    [Fact]
    public void NullText_BecomesEmpty()
    {
        var doc = new TerminalDocument(null!);
        Assert.Equal("", doc.Text);
        Assert.Equal(0, doc.Length);
    }

    [Fact]
    public void Length_IsUtf16Length()
    {
        Assert.Equal(5, new TerminalDocument("hello").Length);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(99, 5)]
    public void ClampOffset_StaysInBounds(int input, int expected)
    {
        Assert.Equal(expected, new TerminalDocument("hello").ClampOffset(input));
    }

    [Fact]
    public void LineIndexForOffset_CountsNewlinesBefore()
    {
        var doc = new TerminalDocument("ab\ncd\nef");
        Assert.Equal(0, doc.LineIndexForOffset(0));
        Assert.Equal(0, doc.LineIndexForOffset(2)); // the '\n' itself is on line 0
        Assert.Equal(1, doc.LineIndexForOffset(3));
        Assert.Equal(2, doc.LineIndexForOffset(7));
        Assert.Equal(2, doc.LineIndexForOffset(999)); // clamped
    }

    [Fact]
    public void LineBounds_ReturnsStartAfterPrevNewline_AndEndAtNextNewline()
    {
        var doc = new TerminalDocument("ab\ncd\nef");
        Assert.Equal((0, 2), doc.LineBounds(1));   // "ab"
        Assert.Equal((3, 5), doc.LineBounds(4));   // "cd"
        Assert.Equal((6, 8), doc.LineBounds(7));   // "ef"
    }

    [Fact]
    public void LineBounds_OnNewlineChar_BelongsToPrecedingLine()
    {
        var doc = new TerminalDocument("ab\ncd");
        Assert.Equal((0, 2), doc.LineBounds(2)); // offset 2 is the '\n'
    }

    [Theory]
    [InlineData(0, 5, "hello")]
    [InlineData(1, 3, "el")]
    [InlineData(-2, 99, "hello")] // clamped both ends
    [InlineData(3, 1, "")]         // start > end yields empty
    public void Slice_ClampsAndOrders(int start, int end, string expected)
    {
        Assert.Equal(expected, new TerminalDocument("hello").Slice(start, end));
    }
}
