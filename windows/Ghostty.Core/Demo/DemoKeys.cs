#if DEMO
using System;
using System.Collections.Generic;

namespace Ghostty.Core.Demo;

/// <summary>
/// Maps friendly key names used by a demo "key" beat to the byte string the
/// terminal expects, injected through the same text path as typed characters.
/// Covers the small set a feature demo realistically needs (Enter, Tab, Esc,
/// Backspace, arrows) without a full scancode encoder. Arrow keys use the
/// normal (non-application) cursor sequences (ESC [ A..D), which is what a
/// fresh shell uses; Backspace sends DEL (0x7f) as modern terminals do.
/// </summary>
internal static class DemoKeys
{
    // Built from char codes (not literals) to keep the control bytes explicit.
    private static readonly string Esc = ((char)0x1b).ToString();
    private static readonly string Del = ((char)0x7f).ToString();

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = "\r",
        ["return"] = "\r",
        ["tab"] = "\t",
        ["escape"] = Esc,
        ["esc"] = Esc,
        ["backspace"] = Del,
        ["up"] = Esc + "[A",
        ["down"] = Esc + "[B",
        ["right"] = Esc + "[C",
        ["left"] = Esc + "[D",
    };

    /// <summary>Returns the byte string for a named key, or null if unknown.</summary>
    public static string? Resolve(string? name)
        => name is not null && Map.TryGetValue(name, out var seq) ? seq : null;
}
#endif
