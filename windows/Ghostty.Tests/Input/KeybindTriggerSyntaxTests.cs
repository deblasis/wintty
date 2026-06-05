using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeybindTriggerSyntaxTests
{
    private static EnumeratedKeybind Kb(params KeybindTrigger[] steps)
        => new(steps, "noop", GhosttyBindingFlags.Consumed);

    [Fact]
    public void Encode_PhysicalChord_UsesKeyEnumNameAndFixedModOrder()
    {
        // super+alt+shift+ctrl input must normalize to ctrl+shift+alt+super; key_t physical = ord 39.
        var s = KeybindTriggerSyntax.Encode(Kb(new KeybindTrigger(0, 39, 1u | 2u | 4u | 8u)));
        Assert.Equal("ctrl+shift+alt+super+key_t", s);
    }

    [Fact]
    public void Encode_Unicode_UsesChar()
    {
        var s = KeybindTriggerSyntax.Encode(Kb(new KeybindTrigger(1, '=', 1u << 2)));
        Assert.Equal("alt+=", s);
    }

    [Fact]
    public void Encode_Sequence_JoinedWithGreaterThan()
    {
        var s = KeybindTriggerSyntax.Encode(Kb(
            new KeybindTrigger(0, 30, 1u << 1),   // ctrl+key_k
            new KeybindTrigger(0, 38, 1u << 1))); // ctrl+key_s
        Assert.Equal("ctrl+key_k>ctrl+key_s", s);
    }

    [Theory]
    [InlineData("ctrl+shift+key_t", "ctrl+shift+key_t")]
    [InlineData("shift+ctrl+key_t", "ctrl+shift+key_t")]   // mod order normalized
    [InlineData("CTRL+Shift+key_t", "ctrl+shift+key_t")]   // case normalized
    [InlineData("control+key_a", "ctrl+key_a")]            // alias control->ctrl
    [InlineData("cmd+key_a", "super+key_a")]               // alias cmd->super
    [InlineData("ctrl+key_k>ctrl+key_s", "ctrl+key_k>ctrl+key_s")] // sequence
    [InlineData("performable:ctrl+key_a", "ctrl+key_a")]            // flag prefix stripped
    [InlineData("global:unconsumed:shift+ctrl+key_t", "ctrl+shift+key_t")] // chained flags stripped
    public void Canonicalize_NormalizesModsAndAliases(string input, string expected)
    {
        Assert.Equal(expected, KeybindTriggerSyntax.Canonicalize(input));
    }

    [Fact]
    public void Canonicalize_FlagPrefixedTrigger_EqualsUnflagged()
    {
        Assert.Equal(
            KeybindTriggerSyntax.Canonicalize("ctrl+key_a"),
            KeybindTriggerSyntax.Canonicalize("performable:ctrl+key_a"));
        Assert.Equal("ctrl+key_a", KeybindTriggerSyntax.Canonicalize("performable:ctrl+key_a"));
    }

    [Fact]
    public void EncodePhysical_BuildsTriggerTokenFromMaskAndOrdinal()
    {
        uint mask = (1u << 1) | (1u << 0); // ctrl + shift
        var ord = KeyNames.OrdinalOf("key_t")!.Value;
        Assert.Equal("ctrl+shift+key_t", KeybindTriggerSyntax.EncodePhysical(mask, ord));
    }

    [Fact]
    public void EncodePhysical_NoMods_IsBareKey()
    {
        var ord = KeyNames.OrdinalOf("f11")!.Value;
        Assert.Equal("f11", KeybindTriggerSyntax.EncodePhysical(0, ord));
    }
}
