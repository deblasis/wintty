using System;
using System.Collections.Generic;
using Ghostty.Core.Interop;

namespace Ghostty.Core.Input;

public enum ConflictKind
{
    None,
    TerminalShadow,
}

/// <summary>A soft-conflict classification for a keybind.</summary>
public readonly record struct KeybindConflict(ConflictKind Kind, string Message)
{
    public static readonly KeybindConflict None = new(ConflictKind.None, string.Empty);

    public bool HasConflict => Kind != ConflictKind.None;
}

/// <summary>
/// Read-only soft-conflict detection over the finalized keybind set. Flags a
/// binding whose trigger shadows a common terminal control code. Hard conflicts
/// (duplicate trigger / sequence-prefix) are pre-resolved by libghostty's
/// finalize and are handled at assign-time in the editing PR, not here.
/// </summary>
public static class KeybindConflictAnalyzer
{
    private const uint ModShift = 1u << 0;
    private const uint ModCtrl = 1u << 1;
    private const uint ModAlt = 1u << 2;
    private const uint ModSuper = 1u << 3;
    private const uint ModShiftRight = 1u << 6;
    private const uint ModCtrlRight = 1u << 7;
    private const uint ModAltRight = 1u << 8;
    private const uint ModSuperRight = 1u << 9;

    private const uint CtrlMask = ModCtrl | ModCtrlRight;
    private const uint OtherMask =
        ModShift | ModShiftRight | ModAlt | ModAltRight | ModSuper | ModSuperRight;

    private const int TagPhysical = 0;
    private const int TagUnicode = 1;

    // Plain Ctrl+<letter> control codes worth warning about.
    private static readonly Dictionary<char, string> Shadows = new()
    {
        ['c'] = "Shadows Ctrl+C (interrupt signal)",
        ['d'] = "Shadows Ctrl+D (end of input)",
        ['z'] = "Shadows Ctrl+Z (suspend)",
        ['l'] = "Shadows Ctrl+L (clear screen)",
        ['s'] = "Shadows Ctrl+S (flow-control stop)",
        ['q'] = "Shadows Ctrl+Q (flow-control resume)",
        ['\\'] = "Shadows Ctrl+\\ (quit signal)",
    };

    public static KeybindConflict Analyze(EnumeratedKeybind kb)
    {
        if (kb.Steps.Count != 1) return KeybindConflict.None;
        if (!kb.Flags.HasFlag(GhosttyBindingFlags.Consumed)) return KeybindConflict.None;

        var step = kb.Steps[0];
        if ((step.Mods & CtrlMask) == 0) return KeybindConflict.None;       // needs Ctrl
        if ((step.Mods & OtherMask) != 0) return KeybindConflict.None;      // Ctrl only

        var ch = ControlChar(step);
        if (ch is char c && Shadows.TryGetValue(c, out var message))
            return new KeybindConflict(ConflictKind.TerminalShadow, message);

        return KeybindConflict.None;
    }

    private static char? ControlChar(KeybindTrigger step)
    {
        switch (step.Tag)
        {
            case TagUnicode:
                return char.ToLowerInvariant((char)step.Key);
            case TagPhysical:
                var name = KeyNames.NameOf((int)step.Key);
                if (name is null) return null;
                if (name.Length == 5 && name.StartsWith("key_", StringComparison.Ordinal))
                    return name[4]; // "key_c" -> 'c'
                if (name == "backslash") return '\\';
                return null;
            default:
                return null;
        }
    }
}
