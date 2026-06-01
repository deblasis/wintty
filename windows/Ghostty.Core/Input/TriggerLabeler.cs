using System;
using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Input;

/// <summary>
/// Renders an EnumeratedKeybind's trigger to a display label like "Ctrl+Shift+T".
/// US-ANSI / current-layout labels; layout-awareness is deferred (cheat sheet).
/// </summary>
public static class TriggerLabeler
{
    // ghostty_input_mods_e bits (authoritative, include/ghostty.h).
    private const uint ModShift = 1u << 0;
    private const uint ModCtrl = 1u << 1;
    private const uint ModAlt = 1u << 2;
    private const uint ModSuper = 1u << 3;
    private const uint ModShiftRight = 1u << 6;
    private const uint ModCtrlRight = 1u << 7;
    private const uint ModAltRight = 1u << 8;
    private const uint ModSuperRight = 1u << 9;

    private const int TagPhysical = 0;
    private const int TagUnicode = 1;
    private const int TagCatchAll = 2;

    private static readonly Dictionary<string, string> Special = new(StringComparer.Ordinal)
    {
        ["backquote"] = "`", ["backslash"] = "\\", ["bracket_left"] = "[", ["bracket_right"] = "]",
        ["comma"] = ",", ["equal"] = "=", ["minus"] = "-", ["period"] = ".", ["quote"] = "'",
        ["semicolon"] = ";", ["slash"] = "/",
        ["space"] = "Space", ["enter"] = "Enter", ["tab"] = "Tab", ["escape"] = "Esc",
        ["delete"] = "Delete", ["backspace"] = "Backspace", ["insert"] = "Insert",
        ["home"] = "Home", ["end"] = "End", ["page_up"] = "Page Up", ["page_down"] = "Page Down",
        ["caps_lock"] = "Caps Lock", ["print_screen"] = "Print Screen", ["scroll_lock"] = "Scroll Lock",
        ["pause"] = "Pause", ["context_menu"] = "Menu",
        ["arrow_up"] = "Up", ["arrow_down"] = "Down", ["arrow_left"] = "Left", ["arrow_right"] = "Right",
    };

    public static string Describe(EnumeratedKeybind kb)
    {
        if (kb.Steps.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (var i = 0; i < kb.Steps.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            AppendStep(sb, kb.Steps[i]);
        }

        return sb.ToString();
    }

    private static void AppendStep(StringBuilder sb, KeybindTrigger step)
    {
        if ((step.Mods & (ModCtrl | ModCtrlRight)) != 0) sb.Append("Ctrl+");
        if ((step.Mods & (ModShift | ModShiftRight)) != 0) sb.Append("Shift+");
        if ((step.Mods & (ModAlt | ModAltRight)) != 0) sb.Append("Alt+");
        if ((step.Mods & (ModSuper | ModSuperRight)) != 0) sb.Append("Win+");
        sb.Append(KeyLabel(step));
    }

    private static string KeyLabel(KeybindTrigger step)
    {
        switch (step.Tag)
        {
            case TagCatchAll:
                return "Any";
            case TagUnicode:
                return char.ConvertFromUtf32((int)step.Key);
            case TagPhysical:
            default:
                var name = KeyNames.NameOf((int)step.Key);
                return name is null ? "?" : LabelForName(name);
        }
    }

    private static string LabelForName(string name)
    {
        if (Special.TryGetValue(name, out var s)) return s;
        if (name.StartsWith("key_", StringComparison.Ordinal)) return name[4..].ToUpperInvariant();
        if (name.StartsWith("digit_", StringComparison.Ordinal)) return name[6..];
        if (name.Length >= 1 && name[0] == 'f' && int.TryParse(name.AsSpan(1), out _)) return name.ToUpperInvariant();
        if (name.StartsWith("numpad_", StringComparison.Ordinal)) return "Num " + TitleCase(name[7..]);
        return TitleCase(name);
    }

    private static string TitleCase(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }
}
