using Ghostty.Core.Accessibility;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Accessibility;

/// <summary>
/// Screen-point-to-offset and span-to-rectangle mapping. Both cross between
/// the trimmed screen document and the padded cell grid, so they share
/// <see cref="ViewportAnchor"/> with the caret and carry the same hazard:
/// a document line is usually SHORTER than Cols.
/// </summary>
public class ViewportHitTestTests
{
    // 10x3 grid, 100px wide and 60px tall => 10px per column, 20px per row,
    // anchored at screen (1000, 500) so an origin mistake cannot look like a
    // correct answer.
    private const int Cols = 10;
    private const int Rows = 3;
    private static readonly ViewportGeometry Geom = new(1000, 500, 100, 60);

    /// <summary>Grid whose rows carry <paramref name="lines"/>, space-padded to Cols.</summary>
    private static CellGrid GridOf(params string[] lines)
    {
        var cells = new Cell[Rows * Cols];
        for (var r = 0; r < Rows; r++)
        {
            var line = r < lines.Length ? lines[r] : "";
            for (var c = 0; c < Cols; c++)
                cells[r * Cols + c] = new Cell(c < line.Length ? line[c] : 0u, 0, 0);
        }
        return new CellGrid(cells, Rows, Cols);
    }

    // read_text trims trailing blanks, so the document is the lines joined by
    // newlines with no padding - deliberately unlike the grid.
    private static TerminalDocument DocOf(params string[] lines) =>
        new(string.Join("\n", lines));

    [Fact]
    public void PointInTheFirstCellMapsToOffsetZero()
    {
        var doc = DocOf("hello", "world");
        var offset = ViewportHitTest.OffsetFromPoint(doc, GridOf("hello", "world"), Geom, 1001, 501);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void PointPicksTheColumnItLandsIn()
    {
        var doc = DocOf("hello", "world");
        // Column 3 of row 0 spans x 1030..1040.
        var offset = ViewportHitTest.OffsetFromPoint(doc, GridOf("hello", "world"), Geom, 1035, 505);
        Assert.Equal(3, offset);
    }

    [Fact]
    public void PointOnTheSecondRowLandsAfterTheNewline()
    {
        var doc = DocOf("hello", "world");
        // Row 1 spans y 520..540; column 0.
        var offset = ViewportHitTest.OffsetFromPoint(doc, GridOf("hello", "world"), Geom, 1001, 525);
        Assert.Equal("hello\n".Length, offset);
    }

    [Fact]
    public void PointPastTheEndOfAShortLineClampsToThatLineNotTheNext()
    {
        var doc = DocOf("hi", "world");
        // Column 8 exists in the grid but "hi" is 2 characters. Landing past it
        // must park at end-of-line, not run into "world".
        var offset = ViewportHitTest.OffsetFromPoint(doc, GridOf("hi", "world"), Geom, 1085, 505);
        Assert.Equal(2, offset);
    }

    [Fact]
    public void PointOutsideTheGridReturnsMinusOne()
    {
        var doc = DocOf("hello", "world");
        var cells = GridOf("hello", "world");
        Assert.Equal(-1, ViewportHitTest.OffsetFromPoint(doc, cells, Geom, 999, 505));
        Assert.Equal(-1, ViewportHitTest.OffsetFromPoint(doc, cells, Geom, 1105, 505));
        Assert.Equal(-1, ViewportHitTest.OffsetFromPoint(doc, cells, Geom, 1050, 499));
        Assert.Equal(-1, ViewportHitTest.OffsetFromPoint(doc, cells, Geom, 1050, 565));
    }

    [Fact]
    public void UnusableGeometryYieldsNoRectangles()
    {
        var doc = DocOf("hello");
        var rects = ViewportHitTest.Rects(doc, GridOf("hello"), new ViewportGeometry(0, 0, 4, 4), new TextSpan(0, 5));
        Assert.Empty(rects);
    }

    [Fact]
    public void SingleLineSpanYieldsOneRectangleOfTheRightCells()
    {
        var doc = DocOf("hello", "world");
        // "ell" = offsets 1..4 on row 0 => x 1010, width 30.
        var rects = ViewportHitTest.Rects(doc, GridOf("hello", "world"), Geom, new TextSpan(1, 4));
        Assert.Equal(4, rects.Length);
        Assert.Equal(1010, rects[0]);
        Assert.Equal(500, rects[1]);
        Assert.Equal(30, rects[2]);
        Assert.Equal(20, rects[3]);
    }

    [Fact]
    public void SpanCrossingLinesYieldsOneRectanglePerLine()
    {
        var doc = DocOf("hello", "world");
        // From "llo" on row 0 through "wo" on row 1.
        var rects = ViewportHitTest.Rects(doc, GridOf("hello", "world"), Geom, new TextSpan(2, 8));
        Assert.Equal(8, rects.Length);
        Assert.Equal(1020, rects[0]);   // row 0 starts at column 2
        Assert.Equal(500, rects[1]);
        Assert.Equal(30, rects[2]);     // columns 2..4
        Assert.Equal(1000, rects[4]);   // row 1 starts at column 0
        Assert.Equal(520, rects[5]);
        Assert.Equal(20, rects[6]);     // columns 0..1
    }

    [Fact]
    public void DegenerateSpanYieldsNoRectangles()
    {
        var doc = DocOf("hello");
        var rects = ViewportHitTest.Rects(doc, GridOf("hello"), Geom, new TextSpan(2, 2));
        Assert.Empty(rects);
    }

    [Fact]
    public void RectanglesAreOmittedForLinesOutsideTheViewport()
    {
        // Document has scrollback the 3-row grid cannot show; the span covers a
        // line above the viewport, which has no on-screen rectangle.
        var doc = DocOf("scrolled", "away", "hello", "world");
        var rects = ViewportHitTest.Rects(doc, GridOf("hello", "world"), Geom, new TextSpan(0, 8));
        Assert.Empty(rects);
    }

    [Fact]
    public void RoundTripsAPointThroughItsOwnRectangle()
    {
        var doc = DocOf("hello", "world");
        var cells = GridOf("hello", "world");
        var offset = ViewportHitTest.OffsetFromPoint(doc, cells, Geom, 1035, 505);
        var rects = ViewportHitTest.Rects(doc, cells, Geom, new TextSpan(offset, offset + 1));
        Assert.Equal(4, rects.Length);
        Assert.True(rects[0] <= 1035 && 1035 <= rects[0] + rects[2]);
        Assert.True(rects[1] <= 505 && 505 <= rects[1] + rects[3]);
    }
}
