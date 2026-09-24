using System;

namespace Ghostty.Core.Input;

/// <summary>
/// The characters a close key leaves behind. On Windows a key press is a
/// KeyDown plus a WM_CHAR that TranslateMessage posts before the KeyDown is
/// even dispatched, so a surface that handles Enter, Escape or Space in its
/// KeyDown to close itself cannot stop the character half: it is already in
/// the queue, and it lands on whatever holds focus once the surface is gone.
/// </summary>
[Flags]
public enum ConsumedCloseChars : byte
{
    None = 0,

    /// <summary>Enter: '\r', or '\n' with Ctrl held.</summary>
    Return = 1,

    /// <summary>Escape: 0x1B.</summary>
    Escape = 2,

    /// <summary>Space: 0x20.</summary>
    Space = 4,
}

/// <summary>What the terminal does with one arriving character.</summary>
public enum CharacterFate : byte
{
    /// <summary>The character goes to the shell.</summary>
    Forward,

    /// <summary>The trailing character of a key another surface consumed to close itself.</summary>
    DroppedByArm,

    /// <summary>The character of a chord this pane's own KeyDown swallowed.</summary>
    DroppedByChordSuppress,
}

/// <summary>
/// A terminal's one-shot guard against the trailing character of a key some
/// other surface consumed to close itself.
///
/// Armed by the surface at the close, before focus moves. Spent only by a
/// character the armed keys produce, which is dropped; anything else flows
/// and leaves the arm standing, so an arm that outlives its keystroke can
/// never eat ordinary typing. Retired by the terminal's next KeyDown and by the start of an IME
/// composition: every real character arrives after its own KeyDown on the
/// surface that receives it (an IME commit is the one exception, and starts
/// a composition first), so an arm still standing at either point belongs to
/// a keystroke whose character went somewhere else.
/// </summary>
public struct ConsumedCloseArm
{
    private ConsumedCloseChars _armed;

    /// <summary>The keys currently armed for, <see cref="ConsumedCloseChars.None"/> when idle.</summary>
    public readonly ConsumedCloseChars Armed => _armed;

    /// <summary>Whether any key is armed.</summary>
    public readonly bool IsArmed => _armed != ConsumedCloseChars.None;

    /// <summary>Adds <paramref name="chars"/> to the arm.</summary>
    public void Arm(ConsumedCloseChars chars) => _armed |= chars;

    /// <summary>Clears the arm. Returns whether one was standing.</summary>
    public bool Retire()
    {
        var was = IsArmed;
        _armed = ConsumedCloseChars.None;
        return was;
    }

    /// <summary>
    /// The arm's decision for one arriving character. Only a character the
    /// armed keys produce spends the arm, and the caller drops exactly that
    /// one. Anything else passes and leaves the arm standing: a dead key
    /// pending when Enter closed the surface posts its spacing accent ahead
    /// of the '\r', and the '\r' is still the one to drop. Staleness is
    /// bounded by the retires (the pane's next KeyDown, an IME composition
    /// start), not by this.
    /// </summary>
    public bool Consume(char ch)
    {
        if (!IsArmed || !Matches(_armed, ch)) return false;
        _armed = ConsumedCloseChars.None;
        return true;
    }

    /// <summary>
    /// The terminal's decision for one arriving character, past its routed
    /// guards. It combines two one-shot drops: the consumed-close arm and the
    /// bound-chord suppress, which OnKeyDown sets when it swallows a chord
    /// so the chord's own WM_CHAR does not reach the shell.
    /// </summary>
    public static CharacterFate Decide(ref ConsumedCloseArm arm, ref bool suppressNext, char ch)
    {
        // Both are asked before either answers. A chord on a close key
        // (Ctrl+Shift+Space) whose action raises the signal stands both drops
        // on its one character; returning on the arm first would leave the
        // chord suppress standing, and it would eat the user's next
        // character, since nothing but a character ever spends it.
        var eaten = arm.Consume(ch);
        if (suppressNext)
        {
            suppressNext = false;
            return eaten ? CharacterFate.DroppedByArm : CharacterFate.DroppedByChordSuppress;
        }
        return eaten ? CharacterFate.DroppedByArm : CharacterFate.Forward;
    }

    /// <summary>Whether <paramref name="ch"/> is a character one of <paramref name="chars"/> produces.</summary>
    public static bool Matches(ConsumedCloseChars chars, char ch) => ch switch
    {
        '\r' or '\n' => (chars & ConsumedCloseChars.Return) != 0,
        '\u001b' => (chars & ConsumedCloseChars.Escape) != 0,
        ' ' => (chars & ConsumedCloseChars.Space) != 0,
        _ => false,
    };

    /// <summary>
    /// The chars a virtual key leaves behind: Enter (0x0D), Escape (0x1B) and
    /// Space (0x20), and <see cref="ConsumedCloseChars.None"/> for anything else.
    /// Takes the raw virtual-key code so this stays free of WinUI types.
    /// </summary>
    public static ConsumedCloseChars ForVirtualKey(int virtualKey) => virtualKey switch
    {
        0x0D => ConsumedCloseChars.Return,
        0x1B => ConsumedCloseChars.Escape,
        0x20 => ConsumedCloseChars.Space,
        _ => ConsumedCloseChars.None,
    };
}
