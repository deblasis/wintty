namespace Ghostty.Core.Input;

/// <summary>
/// Ordinal -> name table mirroring ghostty's input.Key enum (enum(c_int)).
/// The enumerate ABI returns the Key ordinal for physical triggers; this maps
/// it back to the enum name, which TriggerLabeler turns into a display label.
/// Keep in exact order with src/input/key.zig. KeyNamesTests pins the count
/// and several ordinals to catch drift.
/// </summary>
public static class KeyNames
{
    private static readonly string[] Names =
    {
        "unidentified", "backquote", "backslash", "bracket_left", "bracket_right", "comma", "digit_0", "digit_1",
        "digit_2", "digit_3", "digit_4", "digit_5", "digit_6", "digit_7", "digit_8", "digit_9",
        "equal", "intl_backslash", "intl_ro", "intl_yen", "key_a", "key_b", "key_c", "key_d",
        "key_e", "key_f", "key_g", "key_h", "key_i", "key_j", "key_k", "key_l",
        "key_m", "key_n", "key_o", "key_p", "key_q", "key_r", "key_s", "key_t",
        "key_u", "key_v", "key_w", "key_x", "key_y", "key_z", "minus", "period",
        "quote", "semicolon", "slash", "alt_left", "alt_right", "backspace", "caps_lock", "context_menu",
        "control_left", "control_right", "enter", "meta_left", "meta_right", "shift_left", "shift_right", "space",
        "tab", "convert", "kana_mode", "non_convert", "delete", "end", "help", "home",
        "insert", "page_down", "page_up", "arrow_down", "arrow_left", "arrow_right", "arrow_up", "num_lock",
        "numpad_0", "numpad_1", "numpad_2", "numpad_3", "numpad_4", "numpad_5", "numpad_6", "numpad_7",
        "numpad_8", "numpad_9", "numpad_add", "numpad_backspace", "numpad_clear", "numpad_clear_entry", "numpad_comma", "numpad_decimal",
        "numpad_divide", "numpad_enter", "numpad_equal", "numpad_memory_add", "numpad_memory_clear", "numpad_memory_recall", "numpad_memory_store", "numpad_memory_subtract",
        "numpad_multiply", "numpad_paren_left", "numpad_paren_right", "numpad_subtract", "numpad_separator", "numpad_up", "numpad_down", "numpad_right",
        "numpad_left", "numpad_begin", "numpad_home", "numpad_end", "numpad_insert", "numpad_delete", "numpad_page_up", "numpad_page_down",
        "escape", "f1", "f2", "f3", "f4", "f5", "f6", "f7",
        "f8", "f9", "f10", "f11", "f12", "f13", "f14", "f15",
        "f16", "f17", "f18", "f19", "f20", "f21", "f22", "f23",
        "f24", "f25", "fn", "fn_lock", "print_screen", "scroll_lock", "pause", "browser_back", "browser_favorites",
        "browser_forward", "browser_home", "browser_refresh", "browser_search", "browser_stop", "eject", "launch_app_1", "launch_app_2",
        "launch_mail", "media_play_pause", "media_select", "media_stop", "media_track_next", "media_track_previous", "power", "sleep",
        "audio_volume_down", "audio_volume_mute", "audio_volume_up", "wake_up", "copy", "cut", "paste",
    };

    public static int Count => Names.Length;

    private static readonly System.Collections.Generic.Dictionary<string, int> Ordinals = BuildOrdinals();

    private static System.Collections.Generic.Dictionary<string, int> BuildOrdinals()
    {
        var map = new System.Collections.Generic.Dictionary<string, int>(Names.Length, System.StringComparer.Ordinal);
        for (var i = 0; i < Names.Length; i++) map[Names[i]] = i;
        return map;
    }

    /// <summary>Name for an ordinal, or null if out of range.</summary>
    public static string? NameOf(int ordinal)
        => ordinal >= 0 && ordinal < Names.Length ? Names[ordinal] : null;

    /// <summary>Ordinal for an input.Key name, or null if unknown. Inverse of NameOf.</summary>
    public static int? OrdinalOf(string name)
        => Ordinals.TryGetValue(name, out var ordinal) ? ordinal : null;
}
