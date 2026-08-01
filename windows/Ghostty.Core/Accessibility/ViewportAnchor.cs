using System;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Aligns the viewport cell grid with the screen document. The two come from
/// different reads (read_cells and read_text) and use different coordinate
/// spaces, so everything that wants to cross between them needs the same
/// anchor: the last grid row with content is the last document line with
/// content, giving <c>docLine = gridRow + RowShift</c>.
///
/// The document is authoritative for text; the grid is viewport-only and may
/// be a slightly different frame. Callers are expected to validate what they
/// read and decline rather than report something wrong.
/// </summary>
internal readonly struct ViewportAnchor
{
    /// <summary>False when either side has no content, or the grid is
    /// malformed. Nothing can be mapped in that case.</summary>
    public bool HasContent { get; }

    /// <summary><c>docLine = gridRow + RowShift</c>.</summary>
    public int RowShift { get; }

    private ViewportAnchor(bool hasContent, int rowShift)
    {
        HasContent = hasContent;
        RowShift = rowShift;
    }

    public static ViewportAnchor Create(TerminalDocument doc, CellGrid cells)
    {
        // Decline a malformed or concurrently-torn grid (null backing array or a
        // length that disagrees with rows*cols) instead of risking an out-of-range
        // index in a caller. The grid comes from a 500ms cache read off the UI
        // thread, and CellGrid is a multi-field struct, so a partially-published
        // snapshot is possible; report "no content" rather than crash.
        if (cells.Rows <= 0 || cells.Cols <= 0 ||
            cells.Cells is null || cells.Cells.Length < (long)cells.Rows * cells.Cols)
            return new ViewportAnchor(false, 0);

        var lastContentRow = LastContentRow(cells);
        var lastContentDocLine = LastContentDocLine(doc);
        if (lastContentRow < 0 || lastContentDocLine < 0) return new ViewportAnchor(false, 0);

        return new ViewportAnchor(true, lastContentDocLine - lastContentRow);
    }

    /// <summary>
    /// Start offset of a document line, or -1 when the line does not exist.
    /// Linear in the document, which is fine here: the document is a string and
    /// callers are per-query, not per-cell.
    /// </summary>
    public static int LineStartOffset(TerminalDocument doc, int line)
    {
        if (line < 0) return -1;
        if (line == 0) return 0;

        var text = doc.Text;
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (++seen == line) return i + 1;
        }
        return -1;
    }

    // Greatest document line index that contains a non-whitespace character, or -1.
    // Mirrors LastContentRow's "blank" rule (which skips whitespace cells) so the two
    // anchors agree: a trailing whitespace-only line would otherwise shift the anchor
    // by a row and make every mapping off by one.
    private static int LastContentDocLine(TerminalDocument doc)
    {
        var t = doc.Text;
        for (var i = t.Length - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(t[i])) return doc.LineIndexForOffset(i);
        return -1;
    }

    // Greatest grid row index that has a non-blank cell, or -1. A cell is blank
    // when its codepoint is 0 (empty / spacer) or whitespace, matching how
    // read_text trims trailing blank rows.
    private static int LastContentRow(CellGrid cells)
    {
        for (var r = cells.Rows - 1; r >= 0; r--)
            for (var c = 0; c < cells.Cols; c++)
            {
                var cp = cells.Cells[r * cells.Cols + c].Codepoint;
                if (cp != 0 && !(cp <= 0xFFFF && char.IsWhiteSpace((char)cp))) return r;
            }
        return -1;
    }
}
