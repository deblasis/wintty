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

/// <summary>
/// A terminal's one-shot guard against the trailing character of a key some
/// other surface consumed to close itself.
///
/// Armed by the surface at the close, before focus moves. Consumed by the
/// next character, which is dropped only when it is one the armed keys
/// produce, so an arm that outlives its keystroke can never eat ordinary
/// typing. Retired by the terminal's next KeyDown and by the start of an IME
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
    /// The arm's decision for one arriving character. Any character spends
    /// the arm; only a character the armed keys produce is reported as
    /// consumed, and the caller drops exactly that one.
    /// </summary>
    public bool Consume(char ch)
    {
        if (!IsArmed) return false;
        var eaten = Matches(_armed, ch);
        _armed = ConsumedCloseChars.None;
        return eaten;
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
