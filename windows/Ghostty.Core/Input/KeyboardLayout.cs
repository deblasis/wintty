using System.Collections.Generic;

namespace Ghostty.Core.Input;

/// <summary>
/// One key on the visual keyboard. <see cref="KeyName"/> is the input.Key enum
/// name (empty for a spacer). <see cref="Ordinal"/> is its KeyNames ordinal (-1
/// for non-bindable caps/spacers). <see cref="Units"/> is the key width in
/// standard key units (1.0 = a letter key). Non-bindable cells (modifier caps,
/// Fn, spacers) render but never light up — modifiers are chosen via the chips.
/// </summary>
public readonly record struct KeyCell(string KeyName, int Ordinal, string Label, double Units, bool Bindable);

/// <summary>
/// Static ANSI-104 physical layout in three blocks (main, nav cluster, numpad),
/// each a list of rows. The visual map renders these; KeyboardMapModel decides
/// which bindable cells light up for a chosen modifier combo. KeyboardLayoutTests
/// pins ordinal validity and completeness.
/// </summary>
public static class KeyboardLayout
{
    private static KeyCell K(string name, double units = 1.0)
        => new(name, KeyNames.OrdinalOf(name) ?? -1, LabelFor(name), units, Bindable: true);

    private static KeyCell Cap(string name, string label, double units = 1.0)
        => new(name, -1, label, units, Bindable: false);

    private static KeyCell Gap(double units = 0.5) => new("", -1, "", units, Bindable: false);

    private static string LabelFor(string name) => TriggerLabeler.LabelForName(name);

    public static IReadOnlyList<IReadOnlyList<KeyCell>> Main { get; } = new[]
    {
        new[]
        {
            K("escape"), Gap(),
            K("f1"), K("f2"), K("f3"), K("f4"), Gap(),
            K("f5"), K("f6"), K("f7"), K("f8"), Gap(),
            K("f9"), K("f10"), K("f11"), K("f12"),
        },
        new[]
        {
            K("backquote"), K("digit_1"), K("digit_2"), K("digit_3"), K("digit_4"), K("digit_5"),
            K("digit_6"), K("digit_7"), K("digit_8"), K("digit_9"), K("digit_0"),
            K("minus"), K("equal"), K("backspace", 2.0),
        },
        new[]
        {
            K("tab", 1.5), K("key_q"), K("key_w"), K("key_e"), K("key_r"), K("key_t"), K("key_y"),
            K("key_u"), K("key_i"), K("key_o"), K("key_p"),
            K("bracket_left"), K("bracket_right"), K("backslash", 1.5),
        },
        new[]
        {
            Cap("caps_lock", "Caps", 1.75), K("key_a"), K("key_s"), K("key_d"), K("key_f"), K("key_g"),
            K("key_h"), K("key_j"), K("key_k"), K("key_l"), K("semicolon"), K("quote"),
            K("enter", 2.25),
        },
        new[]
        {
            Cap("shift_left", "Shift", 2.25), K("key_z"), K("key_x"), K("key_c"), K("key_v"), K("key_b"),
            K("key_n"), K("key_m"), K("comma"), K("period"), K("slash"),
            Cap("shift_right", "Shift", 2.75),
        },
        new[]
        {
            Cap("control_left", "Ctrl", 1.25), Cap("meta_left", "Win", 1.25), Cap("alt_left", "Alt", 1.25),
            K("space", 6.25),
            Cap("alt_right", "Alt", 1.25), Cap("meta_right", "Win", 1.25),
            K("context_menu", 1.25), Cap("control_right", "Ctrl", 1.25),
        },
    };

    public static IReadOnlyList<IReadOnlyList<KeyCell>> Nav { get; } = new[]
    {
        new[] { K("insert"), K("home"), K("page_up") },
        new[] { K("delete"), K("end"), K("page_down") },
        new[] { Gap(), Gap(), Gap() },
        new[] { Gap(), K("arrow_up"), Gap() },
        new[] { K("arrow_left"), K("arrow_down"), K("arrow_right") },
    };

    public static IReadOnlyList<IReadOnlyList<KeyCell>> Numpad { get; } = new[]
    {
        new[] { Cap("num_lock", "Num", 1.0), K("numpad_divide"), K("numpad_multiply"), K("numpad_subtract") },
        new[] { K("numpad_7"), K("numpad_8"), K("numpad_9"), K("numpad_add") },
        new[] { K("numpad_4"), K("numpad_5"), K("numpad_6") },
        new[] { K("numpad_1"), K("numpad_2"), K("numpad_3"), K("numpad_enter") },
        new[] { K("numpad_0", 2.0), K("numpad_decimal") },
    };
}
