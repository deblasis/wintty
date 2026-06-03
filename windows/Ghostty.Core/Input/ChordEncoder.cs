using System.Collections.Generic;

namespace Ghostty.Core.Input;

/// <summary>
/// Maps a Windows VirtualKey (numeric) + modifier state to a physical ghostty
/// KeybindTrigger for chord capture. US-ANSI layout; returns null for unmapped
/// or modifier-only keys (capture rejects those). Plain ints so Ghostty.Core
/// stays free of WinRT types.
/// </summary>
public static class ChordEncoder
{
    private const uint ModShift = 1u << 0;
    private const uint ModCtrl = 1u << 1;
    private const uint ModAlt = 1u << 2;
    private const uint ModSuper = 1u << 3;

    // VirtualKey numeric -> input.Key enum name. US-ANSI.
    private static readonly Dictionary<int, string> VkToName = BuildVkToName();

    public static KeybindTrigger? TryEncode(int virtualKey, bool ctrl, bool shift, bool alt, bool win)
    {
        if (!VkToName.TryGetValue(virtualKey, out var name)) return null;
        var ordinal = KeyNames.OrdinalOf(name);
        if (ordinal is null) return null;

        uint mods = 0;
        if (ctrl) mods |= ModCtrl;
        if (shift) mods |= ModShift;
        if (alt) mods |= ModAlt;
        if (win) mods |= ModSuper;

        return new KeybindTrigger(0, (uint)ordinal.Value, mods);
    }

    private static Dictionary<int, string> BuildVkToName()
    {
        var m = new Dictionary<int, string>();

        // Letters A-Z (VK 0x41-0x5A) -> key_a..key_z
        for (var vk = 0x41; vk <= 0x5A; vk++)
            m[vk] = "key_" + (char)('a' + (vk - 0x41));

        // Top-row digits 0-9 (VK 0x30-0x39) -> digit_0..digit_9
        for (var vk = 0x30; vk <= 0x39; vk++)
            m[vk] = "digit_" + (char)('0' + (vk - 0x30));

        // Numpad 0-9 (VK 0x60-0x69) -> numpad_0..numpad_9
        for (var vk = 0x60; vk <= 0x69; vk++)
            m[vk] = "numpad_" + (char)('0' + (vk - 0x60));

        // Function keys F1-F24 (VK 0x70-0x87) -> f1..f24
        for (var i = 1; i <= 24; i++)
            m[0x70 + (i - 1)] = "f" + i;

        // Named keys
        m[0x0D] = "enter";
        m[0x09] = "tab";
        m[0x20] = "space";
        m[0x1B] = "escape";
        m[0x08] = "backspace";
        m[0x2E] = "delete";
        m[0x2D] = "insert";
        m[0x24] = "home";
        m[0x23] = "end";
        m[0x21] = "page_up";
        m[0x22] = "page_down";
        m[0x26] = "arrow_up";
        m[0x28] = "arrow_down";
        m[0x25] = "arrow_left";
        m[0x27] = "arrow_right";

        // OEM punctuation (US-ANSI)
        m[0xC0] = "backquote";     // `
        m[0xBD] = "minus";         // -
        m[0xBB] = "equal";         // =
        m[0xBA] = "semicolon";     // ;
        m[0xBF] = "slash";         // /
        m[0xDB] = "bracket_left";  // [
        m[0xDD] = "bracket_right"; // ]
        m[0xDC] = "backslash";     // \
        m[0xDE] = "quote";         // '
        m[0xBC] = "comma";         // ,
        m[0xBE] = "period";        // .

        return m;
    }
}
