using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class FindTests
{
    private static readonly TerminalDocument Doc = new("abc ABC abc"); // len 11

    [Fact]
    public void Find_Forward_FirstMatch()
    {
        Assert.Equal(new TextSpan(0, 3), Doc.Find("abc", 0, Doc.Length, backward: false, ignoreCase: false));
    }

    [Fact]
    public void Find_Backward_LastMatch()
    {
        Assert.Equal(new TextSpan(8, 11), Doc.Find("abc", 0, Doc.Length, backward: true, ignoreCase: false));
    }

    [Fact]
    public void Find_IgnoreCase_MatchesUppercase()
    {
        Assert.Equal(new TextSpan(0, 3), Doc.Find("ABC", 0, Doc.Length, backward: false, ignoreCase: true));
    }

    [Fact]
    public void Find_CaseSensitive_SkipsToExactCase()
    {
        Assert.Equal(new TextSpan(4, 7), Doc.Find("ABC", 0, Doc.Length, backward: false, ignoreCase: false));
    }

    [Fact]
    public void Find_WithinSubRange_OnlySearchesThatRange()
    {
        // Restrict to [4,11): the first "abc" at 0 is excluded.
        Assert.Equal(new TextSpan(8, 11), Doc.Find("abc", 4, 11, backward: false, ignoreCase: false));
    }

    [Fact]
    public void Find_NotFound_ReturnsNull()
    {
        Assert.Null(Doc.Find("zzz", 0, Doc.Length, backward: false, ignoreCase: false));
    }

    [Fact]
    public void Find_EmptyNeedle_ReturnsNull()
    {
        Assert.Null(Doc.Find("", 0, Doc.Length, backward: false, ignoreCase: false));
    }
}
