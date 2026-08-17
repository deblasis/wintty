using System;
using System.Text;

namespace Ghostty.Core.Input;

/// <summary>
/// Encodes one WinUI <c>CharacterReceived</c> UTF-16 code unit into UTF-8
/// for <c>ghostty_surface_text</c>.
///
/// WinUI 3 raises that event once per UTF-16 unit, not once per Unicode
/// scalar. Supplementary-plane characters therefore arrive as a high
/// surrogate followed by a low surrogate. <see cref="Rune(char)"/> throws
/// on either half; that exception on the UI thread is a process kill.
/// This encoder stashes the high surrogate and emits one UTF-8 sequence
/// when the matching low surrogate arrives.
/// </summary>
public static class WmCharUtf8
{
    /// <summary>
    /// Encode <paramref name="ch"/> into <paramref name="dest"/>.
    /// <paramref name="dest"/> must be at least 4 bytes.
    /// Returns <see langword="false"/> when this unit is incomplete
    /// (high surrogate held in <paramref name="pendingHigh"/>) or when a
    /// lone low surrogate is dropped. Does not throw on surrogates.
    /// </summary>
    public static bool TryEncode(char ch, ref char pendingHigh, Span<byte> dest, out int written)
    {
        written = 0;

        if (char.IsHighSurrogate(ch))
        {
            pendingHigh = ch;
            return false;
        }

        Rune rune;
        if (char.IsLowSurrogate(ch))
        {
            var high = pendingHigh;
            pendingHigh = '\0';
            if (!char.IsHighSurrogate(high))
                return false;
            rune = new Rune(high, ch);
        }
        else
        {
            pendingHigh = '\0';
            rune = new Rune(ch);
        }

        written = rune.EncodeToUtf8(dest);
        return true;
    }
}
