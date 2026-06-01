using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class UserKeybindEditorTests
{
    private static EnumeratedKeybind Kb(uint key, uint mods)
        => new(new[] { new KeybindTrigger(0, key, mods) }, "noop", GhosttyBindingFlags.Consumed);

    [Fact]
    public void Unbind_AppendsUnbindLine()
    {
        var result = UserKeybindEditor.Unbind(new[] { "ctrl+key_a=new_tab" }, Kb(39, 1u << 1)); // ctrl+key_t
        Assert.Equal(new[] { "ctrl+key_a=new_tab", "ctrl+key_t=unbind" }, result);
    }

    [Fact]
    public void Unbind_Idempotent()
    {
        var once = UserKeybindEditor.Unbind(System.Array.Empty<string>(), Kb(39, 1u << 1));
        var twice = UserKeybindEditor.Unbind(once, Kb(39, 1u << 1));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Reset_RemovesMatchingTrigger_RegardlessOfSpelling()
    {
        // user lines spell it shift+ctrl; reset target is ctrl+shift+key_t.
        var lines = new[] { "shift+ctrl+key_t=new_window", "ctrl+key_a=new_tab" };
        var result = UserKeybindEditor.Reset(lines, Kb(39, 1u << 1 | 1u << 0));
        Assert.Equal(new[] { "ctrl+key_a=new_tab" }, result);
    }

    [Fact]
    public void Reset_RemovesUnbindLineToo()
    {
        var lines = new[] { "ctrl+key_t=unbind" };
        Assert.Empty(UserKeybindEditor.Reset(lines, Kb(39, 1u << 1)));
    }
}
