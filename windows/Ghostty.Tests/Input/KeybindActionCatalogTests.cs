using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeybindActionCatalogTests
{
    [Theory]
    [InlineData("new_split:right", "Panes", "Split Right")]
    [InlineData("goto_split:left", "Panes", "Focus Split Left")]
    [InlineData("new_tab", "Tabs", "New Tab")]
    [InlineData("copy_to_clipboard:mixed", "Clipboard", "Copy")]
    [InlineData("increase_font_size:1", "Font", "Increase Font Size")]
    [InlineData("toggle_fullscreen", "Window", "Toggle Fullscreen")]
    public void KnownActions_MapToCategoryAndFriendly(string action, string category, string friendly)
    {
        var entry = KeybindActionCatalog.Describe(action);
        Assert.Equal(category, entry.Category);
        Assert.Equal(friendly, entry.Friendly);
    }

    [Fact]
    public void UnknownAction_FallsBackToOtherWithRawName()
    {
        var entry = KeybindActionCatalog.Describe("some_future_action:99");
        Assert.Equal("Other", entry.Category);
        Assert.Equal("some_future_action:99", entry.Friendly);
    }
}
