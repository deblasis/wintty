using System;

namespace Ghostty.Core.Input;

/// <summary>
/// A global-hotkey chord resolved to the Win32 <c>RegisterHotKey</c>
/// surface: a set of <c>fsModifiers</c> bits plus a virtual-key code.
/// Pure logic (no Win32 calls, no I/O) so it unit-tests without the
/// XAML/Win32 runtime; <c>App</c> feeds the resolved pair straight into
/// <c>WindowsGlobalHotKey.Register</c>.
///
/// The accepted string vocabulary mirrors Ghostty's trigger tokens
/// (so the knob feels native to Ghostty users) but maps to Win32 VK
/// codes rather than Ghostty's internal <c>Key</c> enum, because this
/// drives the OS global-hotkey API, not libghostty's keymap.
/// </summary>
public readonly record struct QuickTerminalKeyChord(uint Modifiers, uint VirtualKey)
{
    // Win32 fsModifiers (winuser.h). NoRepeat stops Windows from
    // auto-firing WM_HOTKEY repeatedly while the chord is held.
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// The built-in chord (Ctrl+backtick) used when the config key is
    /// unset or fails to parse. VK_OEM_3 (0xC0) is the US-layout
    /// backtick / tilde key.
    /// </summary>
    public static QuickTerminalKeyChord Default { get; } =
        new(ModControl | ModNoRepeat, 0xC0);

    /// <summary>
    /// Parse a <c>quick-terminal-key</c> value (e.g. "ctrl+backquote",
    /// "alt+space", "ctrl+shift+f1", "f12"). Returns <c>null</c> on any
    /// malformed or unrecognized input so the caller can fall back to
    /// <see cref="Default"/> and log. <see cref="ModNoRepeat"/> is always
    /// included in the result.
    ///
    /// Guard: a modifier-less chord is only accepted for function keys
    /// (F1-F24). Binding a bare printable key (e.g. "a") as a global
    /// hotkey would hijack that key system-wide, which is never what the
    /// user wants from a single config typo, so it is rejected.
    /// </summary>
    public static QuickTerminalKeyChord? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        uint mods = ModNoRepeat;
        uint? vk = null;

        foreach (var part in raw.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim().ToLowerInvariant();
            if (token.Length == 0) return null;

            switch (token)
            {
                case "ctrl":
                case "control":
                    mods |= ModControl;
                    continue;
                case "alt":
                case "opt":
                case "option":
                    mods |= ModAlt;
                    continue;
                case "shift":
                    mods |= ModShift;
                    continue;
                case "super":
                case "cmd":
                case "win":
                case "meta":
                    mods |= ModWin;
                    continue;
            }

            // Non-modifier token: must be the (single) key.
            if (vk is not null) return null; // two keys in one chord
            var resolved = ResolveKey(token);
            if (resolved is null) return null;
            vk = resolved;
        }

        if (vk is null) return null; // modifiers only, no key

        var hasRealModifier = (mods & (ModControl | ModAlt | ModShift | ModWin)) != 0;
        if (!hasRealModifier && !IsFunctionKey(vk.Value)) return null; // foot-gun guard

        return new QuickTerminalKeyChord(mods, vk.Value);
    }

    private static bool IsFunctionKey(uint vk) => vk is >= 0x70 and <= 0x87; // VK_F1..VK_F24

    /// <summary>
    /// Map a single Ghostty-flavoured key token to a Win32 VK code, or
    /// <c>null</c> if unrecognized. Accepts literal single characters
    /// (letters, digits, common punctuation), Ghostty's W3C enum names
    /// (e.g. <c>backquote</c>, <c>bracket_left</c>, <c>arrow_up</c>), and
    /// a handful of friendly aliases (<c>grave_accent</c>, <c>up</c>,
    /// <c>esc</c>, <c>return</c>).
    /// </summary>
    private static uint? ResolveKey(string token)
    {
        // Single literal character (the Ghostty "unicode" trigger path).
        if (token.Length == 1)
        {
            char c = token[0];
            if (c is >= 'a' and <= 'z') return (uint)(0x41 + (c - 'a')); // VK_A..VK_Z
            if (c is >= '0' and <= '9') return (uint)(0x30 + (c - '0')); // VK_0..VK_9
            switch (c)
            {
                case '`': return 0xC0; // VK_OEM_3
                case '-': return 0xBD; // VK_OEM_MINUS
                case '=': return 0xBB; // VK_OEM_PLUS
                case '[': return 0xDB; // VK_OEM_4
                case ']': return 0xDD; // VK_OEM_6
                case '\\': return 0xDC; // VK_OEM_5
                case ';': return 0xBA; // VK_OEM_1
                case '\'': return 0xDE; // VK_OEM_7
                case ',': return 0xBC; // VK_OEM_COMMA
                case '.': return 0xBE; // VK_OEM_PERIOD
                case '/': return 0xBF; // VK_OEM_2
                default: return null;
            }
        }

        // Function keys f1..f24.
        if (token.Length is 2 or 3 && token[0] == 'f'
            && uint.TryParse(token.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return 0x70 + (fn - 1); // VK_F1 = 0x70
        }

        // Named keys (Ghostty W3C enum names + aliases).
        return token switch
        {
            "backquote" or "grave_accent" => 0xC0,
            "minus" => 0xBD,
            "equal" => 0xBB,
            "bracket_left" or "left_bracket" => 0xDB,
            "bracket_right" or "right_bracket" => 0xDD,
            "backslash" => 0xDC,
            "semicolon" => 0xBA,
            "quote" or "apostrophe" => 0xDE,
            "comma" => 0xBC,
            "period" => 0xBE,
            "slash" => 0xBF,
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1Bu,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" => 0x2E,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "page_up" => 0x21,
            "page_down" => 0x22,
            "arrow_up" or "up" => 0x26,
            "arrow_down" or "down" => 0x28,
            "arrow_left" or "left" => 0x25,
            "arrow_right" or "right" => 0x27,
            _ => null,
        };
    }
}
