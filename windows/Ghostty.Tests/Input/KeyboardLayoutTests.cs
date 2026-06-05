using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeyboardLayoutTests
{
    private static IEnumerable<KeyCell> AllCells() =>
        KeyboardLayout.Main.SelectMany(r => r)
            .Concat(KeyboardLayout.Nav.SelectMany(r => r))
            .Concat(KeyboardLayout.Numpad.SelectMany(r => r));

    [Fact]
    public void EveryBindableCellMapsToAValidOrdinal()
    {
        foreach (var cell in AllCells().Where(c => c.Bindable))
        {
            Assert.False(string.IsNullOrEmpty(cell.KeyName));
            Assert.Equal(KeyNames.OrdinalOf(cell.KeyName), cell.Ordinal);
            Assert.NotNull(KeyNames.NameOf(cell.Ordinal));
        }
    }

    [Fact]
    public void NoDuplicateBindableKeys()
    {
        var names = AllCells().Where(c => c.Bindable).Select(c => c.KeyName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void ModifierCapsAreNonBindable()
    {
        var mods = new[] { "control_left", "control_right", "shift_left", "shift_right",
                           "alt_left", "alt_right", "meta_left", "meta_right" };
        foreach (var cell in AllCells().Where(c => mods.Contains(c.KeyName)))
            Assert.False(cell.Bindable);
    }

    [Fact]
    public void IncludesCommonBindableKeys()
    {
        var bindable = AllCells().Where(c => c.Bindable).Select(c => c.KeyName).ToHashSet();
        foreach (var n in new[] { "key_t", "key_a", "f1", "f12", "escape", "enter",
                                  "arrow_up", "digit_1", "numpad_0", "bracket_left" })
            Assert.Contains(n, bindable);
    }
}
