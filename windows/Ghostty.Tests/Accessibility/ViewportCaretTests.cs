using Ghostty.Core.Accessibility;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Accessibility;

/// <summary>
/// The caret maps a viewport cell to a screen-document offset. The document is
/// trimmed (read_text drops trailing blanks) while the grid is not, so the
/// column cannot be treated as an offset within a fixed-width row.
/// </summary>
public class ViewportCaretTests
{
    // A grid whose rows carry the given text, left-aligned and blank-padded to
    // Cols, which is what read_cells reports.
    private static CellGrid Grid(string[] rows, int cols, int curRow, int curCol, bool inViewport)
    {
        var cells = new Cell[rows.Length * cols];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < cols; c++)
                cells[r * cols + c] = new Cell(c < rows[r].Length ? rows[r][c] : ' ', 0, 0);
        return new CellGrid(cells, rows.Length, cols, curRow, curCol, inViewport);
    }

    [Fact]
    public void MapsTheCursorCellToItsDocumentOffset()
    {
        var doc = new TerminalDocument("abc\ndefgh");
        var grid = Grid(new[] { "abc", "defgh" }, 80, curRow: 1, curCol: 2, inViewport: true);

        // Line 1 starts at offset 4 ("abc\n"), column 2 -> 6.
        Assert.Equal(6, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void ClampsTheColumnToTheTrimmedLineNotTheGridWidth()
    {
        // The grid says the row is 80 cells wide; the document line is 3 chars.
        // Parking at column 40 must land at end-of-line, not inside the next line.
        var doc = new TerminalDocument("abc\ndefgh");
        var grid = Grid(new[] { "abc", "defgh" }, 80, curRow: 0, curCol: 40, inViewport: true);

        Assert.Equal(3, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void ReportsEndOfDocumentWhenTheCursorIsBelowTheViewport()
    {
        // The viewport only scrolls up into scrollback, so "not visible" means
        // the cursor is below what is on screen.
        var doc = new TerminalDocument("abc\ndefgh");
        var grid = Grid(new[] { "abc", "defgh" }, 80, curRow: 0, curCol: 0, inViewport: false);

        Assert.Equal(doc.Length, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void ReportsTheOriginForACursorAtTheOrigin()
    {
        // Must not be confused with "no cursor".
        var doc = new TerminalDocument("abc\ndefgh");
        var grid = Grid(new[] { "abc", "defgh" }, 80, curRow: 0, curCol: 0, inViewport: true);

        Assert.Equal(0, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void ADefaultConstructedGridDoesNotClaimACursor()
    {
        // The tab-preview path builds a 3-arg CellGrid; it must not read as a
        // cursor parked at the origin.
        var doc = new TerminalDocument("abc\ndefgh");
        var cells = new Cell[2 * 80];
        var grid = new CellGrid(cells, 2, 80);

        Assert.False(grid.CursorInViewport);
        Assert.Equal(doc.Length, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void AnchorsToTheLastContentRowWhenTheGridIsTallerThanTheDocument()
    {
        // Two content rows at the bottom of a 5-row viewport: the grid's blank
        // leading rows have no document counterpart, so the anchor must come
        // from the last row with content, not from row 0.
        var doc = new TerminalDocument("abc\ndefgh");
        var grid = Grid(new[] { "", "", "", "abc", "defgh" }, 80, curRow: 4, curCol: 1, inViewport: true);

        // Row 4 -> document line 1, which starts at 4; column 1 -> 5.
        Assert.Equal(5, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void FallsBackToEndOfDocumentWhenNothingCanBeAnchored()
    {
        var doc = new TerminalDocument("");
        var grid = Grid(new[] { "" }, 80, curRow: 0, curCol: 0, inViewport: true);

        Assert.Equal(0, ViewportCaret.Offset(doc, grid));
    }

    [Fact]
    public void DeclinesAMalformedGridInsteadOfThrowing()
    {
        var doc = new TerminalDocument("abc");
        // Backing array shorter than Rows*Cols - a torn snapshot.
        var grid = new CellGrid(new Cell[4], 2, 80, 0, 0, true);

        Assert.Equal(doc.Length, ViewportCaret.Offset(doc, grid));
    }
}
