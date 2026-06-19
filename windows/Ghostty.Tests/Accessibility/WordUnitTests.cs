using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class WordUnitTests
{
    // "ab cd  ef" -> word-starts at 0, 3, 7
    private static readonly TerminalDocument Doc = new("ab cd  ef");

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)] // space
    [InlineData(3, true)]
    [InlineData(7, true)]
    public void IsWordStart_DetectsBoundaries(int i, bool expected)
    {
        Assert.Equal(expected, Doc.IsWordStart(i));
    }

    [Fact]
    public void IsWordStart_FalseAcrossNewline()
    {
        // '\n' is whitespace, so the char after it is a word-start, the '\n' is not.
        var d = new TerminalDocument("ab\ncd");
        Assert.False(d.IsWordStart(2)); // the '\n'
        Assert.True(d.IsWordStart(3));  // 'c'
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(4, 3)]
    [InlineData(6, 3)] // in the double space, belongs to the "cd  " unit
    [InlineData(8, 7)]
    public void WordUnitStart_ReturnsContainingWordStart(int offset, int expected)
    {
        Assert.Equal(expected, Doc.WordUnitStart(offset));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 7)]
    [InlineData(7, 9)] // no further word-start -> Length
    public void NextWordStart_FindsNext(int offset, int expected)
    {
        Assert.Equal(expected, Doc.NextWordStart(offset));
    }

    [Theory]
    [InlineData(9, 7)]
    [InlineData(7, 3)]
    [InlineData(3, 0)]
    [InlineData(0, 0)] // already at/before first word-start
    public void PrevWordStart_FindsPrevious(int offset, int expected)
    {
        Assert.Equal(expected, Doc.PrevWordStart(offset));
    }

    [Fact]
    public void LeadingWhitespace_FirstUnitIsTheWhitespace()
    {
        var d = new TerminalDocument("  hi");
        Assert.Equal(0, d.WordUnitStart(1)); // inside leading ws -> 0
        Assert.Equal(2, d.NextWordStart(0)); // first word-start at 2
    }

    [Fact]
    public void ExpandToWord_CoversWordPlusTrailingWhitespace()
    {
        // "ab cd  ef": word-starts 0,3,7. Expanding inside "cd" -> [3,7) = "cd  ".
        Assert.Equal(new TextSpan(3, 7),
            TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(4, 4), TextUnit.Word));
    }

    [Fact]
    public void ExpandToWord_LeadingWhitespaceIsItsOwnUnit()
    {
        var d = new TerminalDocument("  hi");
        Assert.Equal(new TextSpan(0, 2),
            TextRangeNavigator.ExpandToEnclosingUnit(d, new TextSpan(1, 1), TextUnit.Word));
    }

    [Fact]
    public void ExpandToWord_LastWordRunsToEnd()
    {
        Assert.Equal(new TextSpan(7, 9),
            TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(8, 8), TextUnit.Word));
    }

    [Fact]
    public void MoveByWord_ForwardHopsWordStarts()
    {
        Assert.Equal((3, 1), TextRangeNavigator.MoveEndpointByUnit(Doc, 0, TextUnit.Word, 1));
        Assert.Equal((7, 2), TextRangeNavigator.MoveEndpointByUnit(Doc, 0, TextUnit.Word, 2));
    }

    [Fact]
    public void MoveByWord_ForwardClampsPastLastWord()
    {
        // From the last word, the next boundary is end-of-doc; one more move is clamped.
        Assert.Equal((9, 1), TextRangeNavigator.MoveEndpointByUnit(Doc, 7, TextUnit.Word, 1));
        Assert.Equal((9, 0), TextRangeNavigator.MoveEndpointByUnit(Doc, 9, TextUnit.Word, 1));
    }

    [Fact]
    public void MoveByWord_BackwardIsSigned()
    {
        Assert.Equal((3, -1), TextRangeNavigator.MoveEndpointByUnit(Doc, 7, TextUnit.Word, -1));
        Assert.Equal((0, -2), TextRangeNavigator.MoveEndpointByUnit(Doc, 7, TextUnit.Word, -2));
    }
}
