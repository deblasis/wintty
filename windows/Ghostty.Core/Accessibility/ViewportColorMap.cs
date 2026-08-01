using System;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Accessibility;

internal enum ColorResultKind { NotMapped, Uniform, Mixed }

/// <summary>Result of a color query. <c>Rgb</c> (0x00RRGGBB) is only meaningful
/// when <c>Kind == Uniform</c>.</summary>
internal readonly record struct ColorResult(ColorResultKind Kind, uint Rgb)
{
    public static readonly ColorResult NotMapped = new(ColorResultKind.NotMapped, 0);
    public static readonly ColorResult Mixed = new(ColorResultKind.Mixed, 0);
    public static ColorResult Uniform(uint rgb) => new(ColorResultKind.Uniform, rgb);
}

/// <summary>
/// Maps a screen-document text range to viewport cell colors. The viewport grid
/// (from read_cells) is anchored as a suffix of the document: the last grid row
/// that has content corresponds to the last document line that has content.
/// Every queried cell is validated by codepoint against the document character,
/// so any misalignment (scrolled up, soft-wrap, wide chars, a stale snapshot)
/// declines to NotMapped instead of reporting a wrong color. Pure; no platform
/// dependencies. Best-effort and viewport-only by design.
/// </summary>
internal sealed class ViewportColorMap
{
    private readonly TerminalDocument _doc;
    private readonly CellGrid _cells;
    private readonly int _rowShift; // gridRow = docLine - _rowShift
    private readonly bool _hasContent;

    public ViewportColorMap(TerminalDocument doc, CellGrid cells)
    {
        _doc = doc;
        _cells = cells;

        // Shared with the caret mapping so both cross between the grid and the
        // document on exactly the same anchor.
        var anchor = ViewportAnchor.Create(doc, cells);
        _hasContent = anchor.HasContent;
        _rowShift = anchor.RowShift;
    }

    public ColorResult Foreground(TextSpan span) => Reduce(span, fg: true);

    public ColorResult Background(TextSpan span) => Reduce(span, fg: false);

    private ColorResult Reduce(TextSpan span, bool fg)
    {
        if (!_hasContent) return ColorResult.NotMapped;

        var text = _doc.Text;
        var start = _doc.ClampOffset(span.Start);
        var end = _doc.ClampOffset(span.End);
        if (end <= start) return ColorResult.NotMapped;

        // Establish line index and line start once, then maintain them
        // incrementally so the scan is linear in the span length. The column is
        // the UTF-16 distance from the line start: identity with the grid column
        // for BMP text. Astral codepoints take two UTF-16 units but one cell, so
        // the column drifts past them and the codepoint check below declines the
        // range (no wrong color); declining emoji-heavy spans is an accepted
        // best-effort limitation.
        var line = _doc.LineIndexForOffset(start);
        var lineStart = _doc.LineBounds(start).Start;

        uint color = 0;
        var mixed = false;
        var sawCell = false;

        for (var o = start; o < end; o++)
        {
            if (text[o] == '\n') { line++; lineStart = o + 1; continue; }

            var gridRow = line - _rowShift;
            if (gridRow < 0 || gridRow >= _cells.Rows) return ColorResult.NotMapped;

            var col = o - lineStart;
            if (col >= _cells.Cols) return ColorResult.NotMapped;

            var cell = _cells.Cells[gridRow * _cells.Cols + col];
            if (!CodepointMatches(cell.Codepoint, text, o)) return ColorResult.NotMapped;

            var c = fg ? cell.Fg : cell.Bg;
            if (!sawCell) { color = c; sawCell = true; }
            else if (c != color) mixed = true;
        }

        if (!sawCell) return ColorResult.NotMapped;
        return mixed ? ColorResult.Mixed : ColorResult.Uniform(color);
    }

    // True when the cell's codepoint, encoded as UTF-16, matches the document
    // text at `offset`. Empty / wide-spacer cells (codepoint 0) and astral
    // codepoints whose surrogate pair does not line up both return false, which
    // makes the caller decline rather than guess.
    private static bool CodepointMatches(uint codepoint, string text, int offset)
    {
        if (codepoint == 0) return false;
        if (codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF)) return false;
        if (codepoint <= 0xFFFF) return text[offset] == (char)codepoint;

        var s = char.ConvertFromUtf32((int)codepoint);
        return offset + 1 < text.Length && text[offset] == s[0] && text[offset + 1] == s[1];
    }
}
