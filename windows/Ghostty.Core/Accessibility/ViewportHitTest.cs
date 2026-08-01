using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Maps between screen pixels and document offsets, for the two UIA text
/// operations that are geometric rather than textual: hit-testing a point and
/// reporting a range's bounding rectangles.
///
/// Both cross the same seam as <see cref="ViewportCaret"/>: the grid is padded
/// to <c>Cols</c> while the document comes from read_text, which trims trailing
/// blanks. Columns are therefore always clamped to the line they land on, and
/// anything the viewport cannot show reports nothing rather than guessing.
/// </summary>
internal static class ViewportHitTest
{
    /// <summary>
    /// Document offset under a screen point, or -1 when the point is outside
    /// the grid or nothing can be mapped. Callers should treat -1 as "no
    /// range here" rather than substituting offset 0, which would silently
    /// send a screen reader to the top of the screen.
    /// </summary>
    public static int OffsetFromPoint(
        TerminalDocument doc, CellGrid cells, ViewportGeometry geom, double screenX, double screenY)
    {
        if (!geom.IsUsable) return -1;

        var anchor = ViewportAnchor.Create(doc, cells);
        if (!anchor.HasContent) return -1;

        var cellW = geom.Width / cells.Cols;
        var cellH = geom.Height / cells.Rows;
        if (cellW <= 0 || cellH <= 0) return -1;

        var col = (int)Math.Floor((screenX - geom.OriginX) / cellW);
        var row = (int)Math.Floor((screenY - geom.OriginY) / cellH);
        if (col < 0 || col >= cells.Cols || row < 0 || row >= cells.Rows) return -1;

        return OffsetOfCell(doc, anchor, row, col);
    }

    /// <summary>
    /// UIA bounding rectangles for <paramref name="span"/>, as a flat
    /// [x, y, width, height, ...] array with one rectangle per visual line.
    /// Empty for a degenerate span, for unusable geometry, and for lines that
    /// have scrolled out of the viewport - a rectangle for something not on
    /// screen would point a screen reader at the wrong pixels.
    /// </summary>
    public static double[] Rects(
        TerminalDocument doc, CellGrid cells, ViewportGeometry geom, TextSpan span)
    {
        if (span.IsDegenerate || !geom.IsUsable) return Array.Empty<double>();

        var anchor = ViewportAnchor.Create(doc, cells);
        if (!anchor.HasContent) return Array.Empty<double>();

        var cellW = geom.Width / cells.Cols;
        var cellH = geom.Height / cells.Rows;
        if (cellW <= 0 || cellH <= 0) return Array.Empty<double>();

        var start = doc.ClampOffset(span.Start);
        var end = doc.ClampOffset(span.End);
        if (end <= start) return Array.Empty<double>();

        var rects = new List<double>();
        var offset = start;
        while (offset < end)
        {
            var bounds = doc.LineBounds(offset);
            var lineStart = bounds.Start;
            // The newline itself is not a drawn cell; a span that runs through
            // it should not stretch the rectangle one column past the text.
            var lineEnd = bounds.End;
            if (lineEnd > lineStart && doc.Text[lineEnd - 1] == '\n') lineEnd--;

            var segStart = offset;
            var segEnd = Math.Min(end, lineEnd);

            // Row this document line occupies on screen. Lines above the
            // viewport map to a negative row and are skipped, not clamped.
            var gridRow = doc.LineIndexForOffset(lineStart) - anchor.RowShift;
            if (gridRow >= 0 && gridRow < cells.Rows && segEnd > segStart)
            {
                var startCol = Math.Min(segStart - lineStart, cells.Cols);
                var endCol = Math.Min(segEnd - lineStart, cells.Cols);
                if (endCol > startCol)
                {
                    rects.Add(geom.OriginX + startCol * cellW);
                    rects.Add(geom.OriginY + gridRow * cellH);
                    rects.Add((endCol - startCol) * cellW);
                    rects.Add(cellH);
                }
            }

            // Step past the newline; bounds.End already includes it.
            offset = bounds.End > offset ? bounds.End : offset + 1;
        }

        return rects.ToArray();
    }

    // Document offset of a grid cell, clamped into the line it lands on. Mirrors
    // ViewportCaret.Offset - a row that the grid reports as Cols wide is usually
    // a much shorter document line, so a raw lineStart + col would run into the
    // next line's text.
    private static int OffsetOfCell(TerminalDocument doc, ViewportAnchor anchor, int gridRow, int col)
    {
        var lineStart = ViewportAnchor.LineStartOffset(doc, gridRow + anchor.RowShift);
        if (lineStart < 0) return -1;

        var bounds = doc.LineBounds(lineStart);
        var lineEnd = bounds.End;
        if (lineEnd > bounds.Start && doc.Text[lineEnd - 1] == '\n') lineEnd--;

        return Math.Min(lineStart + Math.Max(col, 0), lineEnd);
    }
}
