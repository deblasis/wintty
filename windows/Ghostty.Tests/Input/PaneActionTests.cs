using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class PaneActionTests
{
    [Theory]
    [InlineData(PaneAction.OpenProfile1, 28)]
    [InlineData(PaneAction.OpenProfile2, 29)]
    [InlineData(PaneAction.OpenProfile3, 30)]
    [InlineData(PaneAction.OpenProfile4, 31)]
    [InlineData(PaneAction.OpenProfile5, 32)]
    [InlineData(PaneAction.OpenProfile6, 33)]
    [InlineData(PaneAction.OpenProfile7, 34)]
    [InlineData(PaneAction.OpenProfile8, 35)]
    [InlineData(PaneAction.OpenProfile9, 36)]
    public void OpenProfileN_HasContiguousValuesAfterToggleCommandPalette(PaneAction action, int expected)
    {
        Assert.Equal(expected, (int)action);
    }

    [Fact]
    public void OpenProfile1_IsImmediatelyAfterToggleCommandPalette()
    {
        // Defense in depth alongside the [Theory] above: catches a future
        // commit that inserts a new member between ToggleCommandPalette
        // and OpenProfile1 (e.g. a hypothetical "ToggleSearch = 28") even
        // if the inserter mechanically bumps OpenProfile1..9 to keep
        // their hardcoded values intact.
        Assert.Equal((int)PaneAction.ToggleCommandPalette + 1, (int)PaneAction.OpenProfile1);
    }
}
