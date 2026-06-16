#if DEMO
using Ghostty.Core.Demo;
using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Demo;

public class DemoActionsTests
{
    [Theory]
    [InlineData("split_vertical", PaneAction.SplitVertical)]
    [InlineData("SplitVertical", PaneAction.SplitVertical)]
    [InlineData("new_tab", PaneAction.NewTab)]
    [InlineData("toggle_quick_terminal", PaneAction.ToggleQuickTerminal)]
    public void TryParse_AcceptsUnderscoreAndCaseVariants(string key, PaneAction expected)
    {
        Assert.True(DemoActions.TryParse(key, out var action));
        Assert.Equal(expected, action);
    }

    [Fact]
    public void TryParse_UnknownKey_ReturnsFalse()
    {
        Assert.False(DemoActions.TryParse("not_an_action", out _));
        Assert.False(DemoActions.TryParse(null, out _));
    }

    [Theory]
    [InlineData("8")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("_")]
    [InlineData("__")]
    public void TryParse_NumericOrEmptyInput_ReturnsFalse(string key)
    {
        // Must not parse a raw enum value, and must not throw on all-underscore.
        Assert.False(DemoActions.TryParse(key, out _));
    }
}
#endif
