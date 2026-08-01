using System;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Maps the terminal cursor to an offset in the screen document, for the UIA
/// caret.
///
/// The column is NOT a document offset the way it would be over a padded grid:
/// read_text trims trailing blanks, so a row that the grid reports as 80 cells
/// wide may be a 12-character document line. The column is therefore clamped to
/// the line it lands on, which parks the caret at end-of-line instead of
/// running into the next one.
/// </summary>
internal static class ViewportCaret
{
    /// <summary>
    /// Document offset for the cursor. Returns end-of-document when the cursor
    /// is not in the viewport: the viewport only ever scrolls UP into
    /// scrollback and the cursor lives in the active area, so "not visible"
    /// means it is below what is on screen.
    /// </summary>
    public static int Offset(TerminalDocument doc, CellGrid cells)
    {
        if (!cells.CursorInViewport) return doc.Length;

        var anchor = ViewportAnchor.Create(doc, cells);
        if (!anchor.HasContent) return doc.Length;

        var gridRow = Math.Clamp(cells.CursorRow, 0, cells.Rows - 1);
        var lineStart = ViewportAnchor.LineStartOffset(doc, gridRow + anchor.RowShift);
        if (lineStart < 0) return doc.Length;

        // Clamp into the line, not the grid: the document line is trimmed and
        // is usually shorter than Cols.
        var bounds = doc.LineBounds(lineStart);
        var lineEnd = bounds.End;
        if (lineEnd > bounds.Start && doc.Text[lineEnd - 1] == '\n') lineEnd--;

        var col = Math.Max(cells.CursorCol, 0);
        return Math.Min(lineStart + col, lineEnd);
    }
}
