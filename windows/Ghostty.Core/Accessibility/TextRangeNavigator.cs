namespace Ghostty.Core.Accessibility;

/// <summary>
/// Pure range navigation over a <see cref="TerminalDocument"/>: the math behind
/// UIA's ExpandToEnclosingUnit / MoveEndpointByUnit / CompareEndpoints. Endpoints
/// are UTF-16 offsets; all results are clamped to the document.
/// </summary>
public static class TextRangeNavigator
{
    public static TextSpan ExpandToEnclosingUnit(TerminalDocument doc, TextSpan span, TextUnit unit)
    {
        switch (unit)
        {
            case TextUnit.Document:
                return new TextSpan(0, doc.Length);

            case TextUnit.Line:
                var (start, end) = doc.LineBounds(span.Start);
                return new TextSpan(start, end);

            case TextUnit.Character:
            default:
                var s = doc.ClampOffset(span.Start);
                var e = doc.ClampOffset(s + 1);
                return new TextSpan(s, e);
        }
    }

    /// <summary>
    /// Move a single endpoint by <paramref name="count"/> units. Returns the new
    /// endpoint and the number of units actually moved (0 when fully clamped),
    /// matching UIA's MoveEndpointByUnit contract.
    /// </summary>
    public static (int Endpoint, int Moved) MoveEndpointByUnit(
        TerminalDocument doc, int endpoint, TextUnit unit, int count)
    {
        var from = doc.ClampOffset(endpoint);
        if (count == 0) return (from, 0);

        return unit switch
        {
            TextUnit.Line => MoveByLine(doc, from, count),
            _ => MoveByCharacter(doc, from, count),
        };
    }

    public static int CompareEndpoints(int a, int b) => a.CompareTo(b);

    private static (int, int) MoveByCharacter(TerminalDocument doc, int from, int count)
    {
        var target = doc.ClampOffset(from + count);
        // Report the characters actually traversed (signed), 0 when fully clamped.
        return (target, target - from);
    }

    private static (int, int) MoveByLine(TerminalDocument doc, int from, int count)
    {
        // Normalize to the start of the current line, then walk line starts.
        var (lineStart, _) = doc.LineBounds(from);
        var pos = lineStart;
        var moved = 0;
        var step = count > 0 ? 1 : -1;
        var times = count > 0 ? count : -count;
        for (var i = 0; i < times; i++)
        {
            var next = step > 0 ? NextLineStart(doc, pos) : PrevLineStart(doc, pos);
            if (next == pos) break; // clamped at a boundary
            pos = next;
            moved += step;
        }
        return (pos, moved);
    }

    private static int NextLineStart(TerminalDocument doc, int pos)
    {
        var (_, end) = doc.LineBounds(pos);
        // end is at the '\n' (or Length). Start of the next line is end+1.
        if (end < doc.Length) return end + 1;
        return pos; // already on the last line
    }

    private static int PrevLineStart(TerminalDocument doc, int pos)
    {
        var (start, _) = doc.LineBounds(pos);
        if (start == 0) return pos; // already on the first line
        // start-1 is the previous line's terminating '\n'; find that line's start.
        var (prevStart, _) = doc.LineBounds(start - 1);
        return prevStart;
    }
}
