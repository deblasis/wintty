using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class ChordEncoderTests
{
    private const uint ModShift = 1u << 0;
    private const uint ModCtrl = 1u << 1;
    private const uint ModAlt = 1u << 2;
    private const uint ModSuper = 1u << 3;

    [Fact]
    public void Letter_WithCtrlShift_EncodesPhysicalKeyAndMods()
    {
        // VK 'T' = 0x54.
        var t = ChordEncoder.TryEncode(0x54, ctrl: true, shift: true, alt: false, win: false);
        Assert.NotNull(t);
        Assert.Equal(0, t!.Value.Tag);                 // physical
        Assert.Equal((uint)KeyNames.OrdinalOf("key_t")!, t.Value.Key);
        Assert.Equal(ModCtrl | ModShift, t.Value.Mods);
        // round-trips to ghostty syntax + a friendly label
        var kb = new EnumeratedKeybind(new[] { t.Value }, "x", GhosttyBindingFlags.Consumed);
        Assert.Equal("ctrl+shift+key_t", KeybindTriggerSyntax.Encode(kb));
        Assert.Equal("Ctrl+Shift+T", TriggerLabeler.Describe(kb));
    }

    [Theory]
    [InlineData(0x70, "f1")]      // VK_F1
    [InlineData(0x26, "arrow_up")]// VK_UP
    [InlineData(0xC0, "backquote")] // VK_OEM_3
    [InlineData(0xBD, "minus")]   // VK_OEM_MINUS
    [InlineData(0x0D, "enter")]   // VK_RETURN
    [InlineData(0x30, "digit_0")] // VK '0'
    public void MapsNamedAndOemKeys(int vk, string expectedName)
    {
        var t = ChordEncoder.TryEncode(vk, ctrl: false, shift: false, alt: false, win: false);
        Assert.NotNull(t);
        Assert.Equal((uint)KeyNames.OrdinalOf(expectedName)!, t!.Value.Key);
    }

    [Theory]
    [InlineData(0x10)] // VK_SHIFT (modifier-only)
    [InlineData(0x11)] // VK_CONTROL
    [InlineData(0x12)] // VK_MENU
    [InlineData(0x5B)] // VK_LWIN
    [InlineData(0x07)] // undefined/unmapped
    public void ModifierOnlyOrUnmapped_ReturnsNull(int vk)
    {
        Assert.Null(ChordEncoder.TryEncode(vk, ctrl: true, shift: false, alt: false, win: false));
    }

    [Fact]
    public void Win_MapsToSuper()
    {
        var t = ChordEncoder.TryEncode(0x54, ctrl: false, shift: false, alt: false, win: true);
        Assert.Equal(ModSuper, t!.Value.Mods);
    }
}
