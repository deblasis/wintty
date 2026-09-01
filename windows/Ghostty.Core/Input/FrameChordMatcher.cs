using System.Collections.Generic;
using Ghostty.Core.Interop;

namespace Ghostty.Core.Input;

/// <summary>
/// Answers "is this chord bound, and to what?" against libghostty's parsed
/// keybind set -- the same list the keybind editor and the cheat sheet
/// read, so a user override is matched here too.
///
/// Inside a pane nothing needs this: the key reaches libghostty through the
/// surface and libghostty matches it there. With focus on the frame there is
/// no surface in the focus chain, so the shell has to decide for itself
/// whether a key is a bound chord before it dispatches anything -- and a
/// chord that is not bound must be left strictly alone.
///
/// Pure (no WinRT, no native handles) so the matching rules are unit
/// testable.
/// </summary>
public static class FrameChordMatcher
{
    // Trigger modifier bits, mirroring ghostty_input_mods_e. The right-hand
    // variants name the same modifier, so a binding written with right-ctrl
    // still answers the ctrl a key event reports.
    private const uint ModShift = 1u << 0;
    private const uint ModCtrl = 1u << 1;
    private const uint ModAlt = 1u << 2;
    private const uint ModSuper = 1u << 3;
    private const uint ModShiftRight = 1u << 6;
    private const uint ModCtrlRight = 1u << 7;
    private const uint ModAltRight = 1u << 8;
    private const uint ModSuperRight = 1u << 9;

    private const int TagPhysical = (int)GhosttyTriggerTag.Physical;
    private const int TagUnicode = (int)GhosttyTriggerTag.Unicode;

    /// <summary>
    /// The action string bound to this chord, or null when nothing is.
    /// Single-step binds only: a leader sequence needs a pending-trigger
    /// state machine the frame does not have, and the Windows defaults
    /// ship no leader binds.
    /// </summary>
    public static string? Match(
        IReadOnlyList<EnumeratedKeybind> binds,
        int virtualKey,
        bool ctrl,
        bool shift,
        bool alt,
        bool win)
    {
        var physical = ChordEncoder.TryEncode(virtualKey, ctrl, shift, alt, win);
        var unicode = UnshiftedCodepoint(virtualKey);
        if (physical is null && unicode is null) return null;

        var mods = Query(ctrl, shift, alt, win);
        foreach (var bind in binds)
        {
            if (bind.Steps.Count != 1) continue;
            var step = bind.Steps[0];
            if (Normalize(step.Mods) != mods) continue;

            var hit = step.Tag switch
            {
                TagPhysical => physical is { } p && step.Key == p.Key,
                TagUnicode => unicode is { } cp && step.Key == cp,
                _ => false,
            };
            if (hit) return bind.Action;
        }

        return null;
    }

    /// <summary>
    /// The codepoint a unicode trigger names for this virtual key: the
    /// UNSHIFTED character, because that is what the parser stores --
    /// `ctrl+shift+a` binds 'a', not 'A'. US-ANSI, matching
    /// <see cref="ChordEncoder"/>'s table.
    /// </summary>
    private static uint? UnshiftedCodepoint(int virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A => (uint)('a' + (virtualKey - 0x41)),
        >= 0x30 and <= 0x39 => (uint)('0' + (virtualKey - 0x30)),
        0xC0 => '`',
        0xBD => '-',
        0xBB => '=',
        0xBA => ';',
        0xBF => '/',
        0xDB => '[',
        0xDD => ']',
        0xDC => '\\',
        0xDE => '\'',
        0xBC => ',',
        0xBE => '.',
        _ => null,
    };

    private static uint Query(bool ctrl, bool shift, bool alt, bool win)
    {
        uint mods = 0;
        if (ctrl) mods |= ModCtrl;
        if (shift) mods |= ModShift;
        if (alt) mods |= ModAlt;
        if (win) mods |= ModSuper;
        return mods;
    }

    private static uint Normalize(uint mods)
    {
        uint result = 0;
        if ((mods & (ModShift | ModShiftRight)) != 0) result |= ModShift;
        if ((mods & (ModCtrl | ModCtrlRight)) != 0) result |= ModCtrl;
        if ((mods & (ModAlt | ModAltRight)) != 0) result |= ModAlt;
        if ((mods & (ModSuper | ModSuperRight)) != 0) result |= ModSuper;
        return result;
    }
}
