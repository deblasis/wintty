using System;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Immutable snapshot of the terminal's screen contents as a single string,
/// with line and offset queries used by the UIA text range provider. Offsets
/// are UTF-16 code unit indices into <see cref="Text"/>; lines are delimited
/// by '\n'. Pure; no platform dependencies.
/// </summary>
public sealed class TerminalDocument
{
    public string Text { get; }
    public int Length => Text.Length;

    public TerminalDocument(string text) => Text = text ?? "";

    /// <summary>Clamp an arbitrary offset to <c>[0, Length]</c>.</summary>
    public int ClampOffset(int offset) =>
        offset < 0 ? 0 : (offset > Length ? Length : offset);

    /// <summary>Zero-based line index for an offset (count of '\n' before it).</summary>
    public int LineIndexForOffset(int offset)
    {
        var end = ClampOffset(offset);
        var count = 0;
        for (var i = 0; i < end; i++)
            if (Text[i] == '\n') count++;
        return count;
    }

    /// <summary>
    /// Half-open bounds <c>[start, end)</c> of the line containing <paramref name="offset"/>.
    /// <c>start</c> is just after the previous '\n' (or 0); <c>end</c> is the next
    /// '\n' (or Length). A '\n' character belongs to the line it terminates.
    /// </summary>
    public (int Start, int End) LineBounds(int offset)
    {
        var o = ClampOffset(offset);
        var start = o;
        while (start > 0 && Text[start - 1] != '\n') start--;
        var end = o;
        while (end < Length && Text[end] != '\n') end++;
        return (start, end);
    }

    /// <summary>Substring for <c>[start, end)</c>, clamped; empty if start &gt;= end.</summary>
    public string Slice(int start, int end)
    {
        var s = ClampOffset(start);
        var e = ClampOffset(end);
        return e <= s ? "" : Text.Substring(s, e - s);
    }

    /// <summary>
    /// True when <paramref name="i"/> begins a word: a non-whitespace char whose
    /// previous char is whitespace (or the start of the document). Whitespace is
    /// <see cref="char.IsWhiteSpace(char)"/>, which includes '\n', so words never
    /// cross a line boundary.
    /// </summary>
    public bool IsWordStart(int i) =>
        i >= 0 && i < Length && !char.IsWhiteSpace(Text[i]) && (i == 0 || char.IsWhiteSpace(Text[i - 1]));

    /// <summary>
    /// Start of the word unit containing <paramref name="offset"/>: the greatest
    /// word-start at or before it, or 0 (leading whitespace forms its own unit).
    /// </summary>
    public int WordUnitStart(int offset)
    {
        var o = ClampOffset(offset);
        for (var i = o; i >= 1; i--)
            if (IsWordStart(i)) return i;
        return 0;
    }

    /// <summary>First word-start strictly after <paramref name="offset"/>, else Length.</summary>
    public int NextWordStart(int offset)
    {
        var o = ClampOffset(offset);
        for (var i = o + 1; i < Length; i++)
            if (IsWordStart(i)) return i;
        return Length;
    }

    /// <summary>Greatest word-start strictly before <paramref name="offset"/>, else 0.</summary>
    public int PrevWordStart(int offset)
    {
        var o = ClampOffset(offset);
        for (var i = o - 1; i >= 1; i--)
            if (IsWordStart(i)) return i;
        return 0;
    }

    /// <summary>
    /// Find <paramref name="text"/> within <c>[withinStart, withinEnd)</c>. Returns the
    /// first match (or the last when <paramref name="backward"/>), or null when not found
    /// or the needle is empty. Comparison is Ordinal, or OrdinalIgnoreCase when requested.
    /// </summary>
    public TextSpan? Find(string text, int withinStart, int withinEnd, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = ClampOffset(withinStart);
        var e = ClampOffset(withinEnd);
        if (e <= s || text.Length > e - s) return null;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Search a span over the window so a find on the whole document does not
        // copy the screen text.
        var window = Text.AsSpan(s, e - s);
        var needle = text.AsSpan();
        var rel = backward ? window.LastIndexOf(needle, comparison) : window.IndexOf(needle, comparison);
        return rel < 0 ? null : new TextSpan(s + rel, s + rel + text.Length);
    }
}
