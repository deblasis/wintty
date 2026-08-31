using System.Linq;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
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

    [Fact]
    public void AllActions_DedupesAndDescribes()
    {
        var binds = new[]
        {
            MakeBind("new_tab"),
            MakeBind("new_tab"),          // dup
            MakeBind("new_split:right"),
            MakeBind("copy_to_clipboard"),
        };
        var actions = KeybindActionCatalog.AllActions(binds);

        Assert.Equal(3, actions.Count);
        Assert.Contains(actions, a => a.RawAction == "new_tab" && a.Friendly == "New Tab" && a.Category == "Tabs");
        Assert.Contains(actions, a => a.RawAction == "new_split:right" && a.Friendly == "Split Right");
    }

    // The pin/group verbs are bindable (a user keybind line can name them)
    // but ship no default chord, so the only rows they can ever produce are
    // ones the user bound. Describe is the whole cheat-sheet wiring: it is
    // what turns the raw verb into the row the dialog renders.
    [Theory]
    [InlineData("pin_tab", "Tabs", "Pin Tab")]
    [InlineData("unpin_tab", "Tabs", "Unpin Tab")]
    [InlineData("move_group:left", "Groups", "Move Group Left")]
    [InlineData("move_group:right", "Groups", "Move Group Right")]
    public void TabShellVerbs_RenderWithCategoryAndFriendly(string action, string category, string friendly)
    {
        var entry = KeybindActionCatalog.Describe(action);
        Assert.Equal(category, entry.Category);
        Assert.Equal(friendly, entry.Friendly);
    }

    // Directionless move_group keeps its bare friendly, like move_tab.
    [Fact]
    public void MoveGroup_WithoutArgument_RendersBare()
    {
        var entry = KeybindActionCatalog.Describe("move_group");
        Assert.Equal("Groups", entry.Category);
        Assert.Equal("Move Group", entry.Friendly);
    }

    [Fact]
    public void TabShellVerbs_AreOfferedOnceBound()
    {
        var binds = new[]
        {
            MakeBind("pin_tab"),
            MakeBind("move_group:left"),
        };
        var actions = KeybindActionCatalog.AllActions(binds);

        Assert.Contains(actions, a => a.RawAction == "pin_tab" && a.Friendly == "Pin Tab");
        Assert.Contains(actions, a => a.RawAction == "move_group:left" && a.Friendly == "Move Group Left");
    }

    private static EnumeratedKeybind MakeBind(string action) =>
        new(new[] { new KeybindTrigger(0, (uint)KeyNames.OrdinalOf("key_a")!.Value, 1u << 1) },
            action, GhosttyBindingFlags.Consumed);
}
