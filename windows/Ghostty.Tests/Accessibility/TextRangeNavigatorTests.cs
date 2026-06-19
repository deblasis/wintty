using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class TextRangeNavigatorTests
{
    private static readonly TerminalDocument Doc = new("ab\ncd\nef"); // 3 lines, len 8

    [Fact]
    public void ExpandToDocument_CoversWholeText()
    {
        var span = TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(4, 4), TextUnit.Document);
        Assert.Equal(new TextSpan(0, 8), span);
    }

    [Fact]
    public void ExpandToLine_CoversContainingLine()
    {
        var span = TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(4, 4), TextUnit.Line);
        Assert.Equal(new TextSpan(3, 5), span); // "cd"
    }

    [Fact]
    public void ExpandToCharacter_DegenerateBecomesSingleChar()
    {
        var span = TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(1, 1), TextUnit.Character);
        Assert.Equal(new TextSpan(1, 2), span);
    }

    [Fact]
    public void ExpandToCharacter_AtEnd_StaysDegenerate()
    {
        var span = TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(8, 8), TextUnit.Character);
        Assert.Equal(new TextSpan(8, 8), span);
    }

    [Fact]
    public void MoveEndpointByCharacter_ClampsAtBounds()
    {
        Assert.Equal((2, 2), TextRangeNavigator.MoveEndpointByUnit(Doc, 0, TextUnit.Character, 2));
        Assert.Equal((0, 0), TextRangeNavigator.MoveEndpointByUnit(Doc, 0, TextUnit.Character, -5)); // clamped, moved 0
        Assert.Equal((8, 0), TextRangeNavigator.MoveEndpointByUnit(Doc, 8, TextUnit.Character, 3));  // clamped, moved 0
    }

    [Fact]
    public void MoveEndpointByLine_MovesToLineStart()
    {
        // from offset 4 (line 1) forward one line -> start of line 2 (offset 6)
        Assert.Equal((6, 1), TextRangeNavigator.MoveEndpointByUnit(Doc, 4, TextUnit.Line, 1));
        // backward one line -> start of line 0 (offset 0); moved is signed (UIA convention)
        Assert.Equal((0, -1), TextRangeNavigator.MoveEndpointByUnit(Doc, 4, TextUnit.Line, -1));
    }

    [Fact]
    public void CompareEndpoints_OrdersByOffset()
    {
        Assert.True(TextRangeNavigator.CompareEndpoints(3, 5) < 0);
        Assert.Equal(0, TextRangeNavigator.CompareEndpoints(4, 4));
        Assert.True(TextRangeNavigator.CompareEndpoints(7, 2) > 0);
    }
}
