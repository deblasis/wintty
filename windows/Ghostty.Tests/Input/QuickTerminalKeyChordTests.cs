using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class QuickTerminalKeyChordTests
{
    // Win32 fsModifiers, repeated here so the test is independent of the
    // type under test (a copy-paste bug in the constants is caught).
    private const uint Alt = 0x0001;
    private const uint Control = 0x0002;
    private const uint Shift = 0x0004;
    private const uint Win = 0x0008;
    private const uint NoRepeat = 0x4000;

    [Fact]
    public void Default_Is_Ctrl_Backquote_NoRepeat()
    {
        var d = QuickTerminalKeyChord.Default;
        Assert.Equal(Control | NoRepeat, d.Modifiers);
        Assert.Equal(0xC0u, d.VirtualKey); // VK_OEM_3
    }

    [Theory]
    [InlineData("ctrl+backquote", Control | NoRepeat, 0xC0u)]
    [InlineData("ctrl+grave_accent", Control | NoRepeat, 0xC0u)] // alias
    [InlineData("ctrl+`", Control | NoRepeat, 0xC0u)]            // literal char
    [InlineData("control+backquote", Control | NoRepeat, 0xC0u)] // long modifier name
    [InlineData("alt+space", Alt | NoRepeat, 0x20u)]
    [InlineData("ctrl+shift+f1", Control | Shift | NoRepeat, 0x70u)]
    [InlineData("super+a", Win | NoRepeat, 0x41u)]
    [InlineData("win+a", Win | NoRepeat, 0x41u)]
    [InlineData("ctrl+1", Control | NoRepeat, 0x31u)]
    [InlineData("ctrl+escape", Control | NoRepeat, 0x1Bu)]
    [InlineData("ctrl+arrow_up", Control | NoRepeat, 0x26u)]
    [InlineData("ctrl+up", Control | NoRepeat, 0x26u)]           // arrow alias
    [InlineData("CTRL+BackQuote", Control | NoRepeat, 0xC0u)]    // case-insensitive
    [InlineData("ctrl + backquote", Control | NoRepeat, 0xC0u)]  // surrounding spaces
    [InlineData("f12", NoRepeat, 0x7Bu)]                         // modifier-less function key OK
    public void Parse_Valid(string input, uint expectedMods, uint expectedVk)
    {
        var chord = QuickTerminalKeyChord.Parse(input);
        Assert.NotNull(chord);
        Assert.Equal(expectedMods, chord!.Value.Modifiers);
        Assert.Equal(expectedVk, chord.Value.VirtualKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl")]              // modifier only, no key
    [InlineData("ctrl+shift")]        // modifiers only
    [InlineData("ctrl+nonsense")]     // unknown key token
    [InlineData("ctrl+a+b")]          // two non-modifier keys
    [InlineData("a")]                 // modifier-less printable key (foot-gun guard)
    [InlineData("ctrl+ctrl+a")]       // see note: short-circuited, asserted in dedicated fact
    public void Parse_Invalid_Returns_Null(string? input)
    {
        // "ctrl+ctrl+a" is intentionally valid (dup modifier, key a); it is
        // excluded here and asserted in the dedicated fact below.
        if (input == "ctrl+ctrl+a") return;
        Assert.Null(QuickTerminalKeyChord.Parse(input));
    }

    [Fact]
    public void Parse_Duplicate_Modifier_Is_Idempotent()
    {
        var chord = QuickTerminalKeyChord.Parse("ctrl+ctrl+a");
        Assert.NotNull(chord);
        Assert.Equal(Control | NoRepeat, chord!.Value.Modifiers);
        Assert.Equal(0x41u, chord.Value.VirtualKey);
    }
}
