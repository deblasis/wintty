using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ghostty.Core.Input;

/// <summary>
/// Encodes a keybind trigger to ghostty config syntax (e.g. "ctrl+shift+key_t")
/// and canonicalizes a raw trigger token so equivalent spellings compare equal.
/// ghostty's Trigger.parse matches a key token against the input.Key enum field
/// name directly, so the KeyNames name round-trips as a physical key.
/// </summary>
public static class KeybindTriggerSyntax
{
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

    // Fixed emission order. Maps a normalized mod token -> its rank.
    private static readonly string[] ModOrder = { "ctrl", "shift", "alt", "super" };

    private static readonly Dictionary<string, string> ModAliases = new(StringComparer.Ordinal)
    {
        ["ctrl"] = "ctrl", ["control"] = "ctrl",
        ["shift"] = "shift",
        ["alt"] = "alt", ["opt"] = "alt", ["option"] = "alt",
        ["super"] = "super", ["cmd"] = "super", ["command"] = "super", ["win"] = "super",
    };

    public static string Encode(EnumeratedKeybind kb)
        => string.Join(">", kb.Steps.Select(EncodeStep));

    private static string EncodeStep(KeybindTrigger step)
    {
        var sb = new StringBuilder();
        if ((step.Mods & (ModCtrl | ModCtrlRight)) != 0) sb.Append("ctrl+");
        if ((step.Mods & (ModShift | ModShiftRight)) != 0) sb.Append("shift+");
        if ((step.Mods & (ModAlt | ModAltRight)) != 0) sb.Append("alt+");
        if ((step.Mods & (ModSuper | ModSuperRight)) != 0) sb.Append("super+");
        sb.Append(KeyToken(step));
        return sb.ToString();
    }

    private static string KeyToken(KeybindTrigger step) => step.Tag switch
    {
        TagUnicode => char.ConvertFromUtf32((int)step.Key),
        TagCatchAll => "catch_all",
        _ => KeyNames.NameOf((int)step.Key) ?? "unidentified",
    };

    /// <summary>Normalize a raw trigger token (possibly a sequence) for equality compare.</summary>
    public static string Canonicalize(string token)
        => string.Join(">", token.Split('>').Select(CanonicalizeStep));

    private static string CanonicalizeStep(string step)
    {
        var mods = new List<string>();
        string? key = null;
        foreach (var raw in step.Split('+'))
        {
            var part = raw.Trim().ToLowerInvariant();
            if (part.Length == 0) { key = "+"; continue; } // literal '+'
            if (ModAliases.TryGetValue(part, out var mod)) mods.Add(mod);
            else key = part; // last non-mod token wins
        }

        var ordered = ModOrder.Where(mods.Contains);
        return key is null ? string.Join("+", ordered) : string.Join("+", ordered.Append(key));
    }
}
