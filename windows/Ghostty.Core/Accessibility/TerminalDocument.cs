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
}
