using System;
using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Tabs;

/// <summary>One resolved cell: codepoint (0 = empty) + 0x00RRGGBB fg/bg.</summary>
internal readonly record struct Cell(uint Codepoint, uint Fg, uint Bg);

/// <summary>
/// The viewport as row-major cells. <c>Cells.Length == Rows*Cols</c>.
/// The cursor fields default to "no cursor" so callers that only want cells
/// (the tab preview) keep constructing this with three arguments and do not
/// accidentally claim a cursor sitting at the origin.
/// </summary>
internal readonly record struct CellGrid(
    Cell[] Cells,
    int Rows,
    int Cols,
    int CursorRow = 0,
    int CursorCol = 0,
    bool CursorInViewport = false);

/// <summary>A run of same-colored text within a preview line.</summary>
internal readonly record struct PreviewRun(string Text, uint Fg, uint Bg);

/// <summary>One preview line: an ordered list of colored runs.</summary>
internal sealed class PreviewLine
{
    private readonly List<PreviewRun> _runs = new();
    public IReadOnlyList<PreviewRun> Runs => _runs;
    public int Count => _runs.Count;
    public PreviewRun this[int i] => _runs[i];
    internal void Add(PreviewRun r) => _runs.Add(r);
}

/// <summary>
/// Coalesces a resolved cell grid into colored display lines for a preview tile:
/// per row build runs of consecutive same-(fg,bg) cells (skipping empty cells and
/// C0 controls, keeping PUA glyphs), clip to <paramref name="maxCols"/> cells,
/// drop trailing blank rows, keep the last <paramref name="maxRows"/>. Pure.
/// </summary>
internal static class CellGridFormatter
{
    public static IReadOnlyList<PreviewLine> Format(CellGrid grid, int maxRows, int maxCols)
    {
        if (grid.Rows <= 0 || grid.Cols <= 0 || maxRows <= 0 || maxCols <= 0)
            return Array.Empty<PreviewLine>();

        // Build a line per row first so we can drop trailing blanks.
        var all = new List<PreviewLine>(grid.Rows);
        for (var r = 0; r < grid.Rows; r++)
            all.Add(BuildLine(grid, r, maxCols));

        var end = all.Count;
        while (end > 0 && all[end - 1].Count == 0) end--;
        if (end == 0) return Array.Empty<PreviewLine>();

        var start = Math.Max(0, end - maxRows);
        return all.GetRange(start, end - start);
    }

    private static PreviewLine BuildLine(CellGrid grid, int row, int maxCols)
    {
        var line = new PreviewLine();
        var sb = new StringBuilder();
        var haveRun = false;
        uint runFg = 0, runBg = 0;

        void Flush()
        {
            if (sb.Length > 0) line.Add(new PreviewRun(sb.ToString(), runFg, runBg));
            sb.Clear();
        }

        var cols = Math.Min(grid.Cols, maxCols);
        for (var c = 0; c < cols; c++)
        {
            var cell = grid.Cells[row * grid.Cols + c];
            var cp = cell.Codepoint;
            if (cp == 0) continue;                              // empty / wide spacer
            if (cp < 0x20 || cp == 0x7F) continue;              // C0 controls (keep PUA)
            if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) continue; // invalid codepoint
            if (!haveRun || cell.Fg != runFg || cell.Bg != runBg)
            {
                Flush();
                runFg = cell.Fg; runBg = cell.Bg; haveRun = true;
            }
            sb.Append(char.ConvertFromUtf32((int)cp));
        }
        Flush();
        return line;
    }
}
