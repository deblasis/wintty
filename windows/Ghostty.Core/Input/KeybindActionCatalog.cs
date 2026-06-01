using System;
using System.Collections.Generic;

namespace Ghostty.Core.Input;

/// <summary>Category + human-friendly name resolved for a keybind action.</summary>
public readonly record struct ActionDescription(string Category, string Friendly);

/// <summary>
/// Maps a keybind action string (e.g. "new_split:right") to a UI category and a
/// friendly name. Matches on the verb before ':'. Unmapped actions fall back to
/// the raw action under "Other". Covers the curated Windows default set; extend
/// as needed.
/// </summary>
public static class KeybindActionCatalog
{
    // verb -> (category, friendly). For verbs whose argument matters (splits,
    // goto_split, resize_split) the argument is appended in Describe.
    private static readonly Dictionary<string, (string Category, string Friendly)> Verbs =
        new(StringComparer.Ordinal)
    {
        ["new_split"] = ("Panes", "Split"),
        ["goto_split"] = ("Panes", "Focus Split"),
        ["resize_split"] = ("Panes", "Resize Split"),
        ["equalize_splits"] = ("Panes", "Equalize Splits"),
        ["toggle_split_zoom"] = ("Panes", "Toggle Split Zoom"),
        ["new_tab"] = ("Tabs", "New Tab"),
        ["close_tab"] = ("Tabs", "Close Tab"),
        ["previous_tab"] = ("Tabs", "Previous Tab"),
        ["next_tab"] = ("Tabs", "Next Tab"),
        ["goto_tab"] = ("Tabs", "Go To Tab"),
        ["move_tab"] = ("Tabs", "Move Tab"),
        ["last_tab"] = ("Tabs", "Last Tab"),
        ["scroll_page_up"] = ("Scroll", "Scroll Page Up"),
        ["scroll_page_down"] = ("Scroll", "Scroll Page Down"),
        ["scroll_to_top"] = ("Scroll", "Scroll To Top"),
        ["scroll_to_bottom"] = ("Scroll", "Scroll To Bottom"),
        ["jump_to_prompt"] = ("Scroll", "Jump To Prompt"),
        ["copy_to_clipboard"] = ("Clipboard", "Copy"),
        ["paste_from_clipboard"] = ("Clipboard", "Paste"),
        ["paste_from_selection"] = ("Clipboard", "Paste Selection"),
        ["select_all"] = ("Clipboard", "Select All"),
        ["adjust_selection"] = ("Clipboard", "Adjust Selection"),
        ["increase_font_size"] = ("Font", "Increase Font Size"),
        ["decrease_font_size"] = ("Font", "Decrease Font Size"),
        ["reset_font_size"] = ("Font", "Reset Font Size"),
        ["toggle_fullscreen"] = ("Window", "Toggle Fullscreen"),
        ["toggle_quick_terminal"] = ("Window", "Toggle Quick Terminal"),
        ["toggle_command_palette"] = ("Window", "Command Palette"),
        ["new_window"] = ("Window", "New Window"),
        ["reload_config"] = ("App", "Reload Config"),
        ["open_config"] = ("App", "Open Config"),
        ["quit"] = ("App", "Quit"),
        ["start_search"] = ("Search", "Find"),
        ["write_screen_file"] = ("Terminal", "Write Screen To File"),
    };

    // Friendly suffix for directional/indexed arguments, where it reads well.
    private static readonly Dictionary<string, string> ArgLabel = new(StringComparer.Ordinal)
    {
        ["right"] = "Right", ["left"] = "Left", ["up"] = "Up", ["down"] = "Down",
    };

    public static ActionDescription Describe(string action)
    {
        if (string.IsNullOrEmpty(action)) return new ActionDescription("Other", action ?? string.Empty);

        var colon = action.IndexOf(':');
        var verb = colon < 0 ? action : action[..colon];
        var arg = colon < 0 ? null : action[(colon + 1)..];

        if (!Verbs.TryGetValue(verb, out var hit))
            return new ActionDescription("Other", action);

        var friendly = hit.Friendly;
        // Append a direction word for split/focus/resize so each row is distinct.
        if (arg is not null && ArgLabel.TryGetValue(arg, out var dir)
            && (verb == "new_split" || verb == "goto_split" || verb == "resize_split"))
        {
            friendly = $"{hit.Friendly} {dir}";
        }

        return new ActionDescription(hit.Category, friendly);
    }
}
