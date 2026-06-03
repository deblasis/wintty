using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeybindConflictsTests
{
    private static EnumeratedKeybind Kb(string action, uint key, uint mods)
        => new(new[] { new KeybindTrigger(0, key, mods) }, action, GhosttyBindingFlags.Consumed);

    [Fact]
    public void FindByTrigger_ReturnsExistingBindingAtChord()
    {
        var binds = new List<EnumeratedKeybind>
        {
            Kb("new_tab", 39, 1u << 1),   // ctrl+key_t
            Kb("copy_to_clipboard:mixed", 22, 1u << 1),
        };
        var hit = KeybindConflicts.FindByTrigger(binds, "ctrl+key_t");
        Assert.NotNull(hit);
        Assert.Equal("new_tab", hit!.Action);
    }

    [Fact]
    public void FindByTrigger_NoMatch_ReturnsNull()
    {
        var binds = new List<EnumeratedKeybind> { Kb("new_tab", 39, 1u << 1) };
        Assert.Null(KeybindConflicts.FindByTrigger(binds, "ctrl+key_w"));
    }
}
