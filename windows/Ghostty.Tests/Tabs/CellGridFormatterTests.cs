using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class CellGridFormatterTests
{
    // Build a grid from rows of (codepoint, fg, bg) tuples padded to cols.
    private static CellGrid Grid(int cols, params (uint cp, uint fg, uint bg)[][] rows)
    {
        var cells = new Cell[rows.Length * cols];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < cols; c++)
            {
                var src = c < rows[r].Length ? rows[r][c] : (0u, 0u, 0u);
                cells[r * cols + c] = new Cell(src.Item1, src.Item2, src.Item3);
            }
        return new CellGrid(cells, rows.Length, cols);
    }

    private const uint A = 'a', B = 'b', C = 'c', D = 'd', X = 'x', Y = 'y';

    [Fact]
    public void Single_run_when_colors_match()
    {
        var g = Grid(3, new[] { (A, 1u, 2u), (B, 1u, 2u), (C, 1u, 2u) });
        var lines = CellGridFormatter.Format(g, maxRows: 5, maxCols: 10);
        var run = Assert.Single(lines[0].Runs);
        Assert.Equal("abc", run.Text);
        Assert.Equal(1u, run.Fg);
        Assert.Equal(2u, run.Bg);
    }

    [Fact]
    public void Splits_run_on_color_change()
    {
        var g = Grid(2, new[] { (A, 1u, 0u), (B, 9u, 0u) });
        var runs = CellGridFormatter.Format(g, 5, 10)[0].Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal("a", runs[0].Text); Assert.Equal(1u, runs[0].Fg);
        Assert.Equal("b", runs[1].Text); Assert.Equal(9u, runs[1].Fg);
    }

    [Fact]
    public void Skips_zero_codepoint_cells()
    {
        var g = Grid(3, new[] { (A, 1u, 0u), (0u, 1u, 0u), (C, 1u, 0u) });
        var run = Assert.Single(CellGridFormatter.Format(g, 5, 10)[0].Runs);
        Assert.Equal("ac", run.Text);
    }

    [Fact]
    public void Drops_trailing_blank_rows_and_keeps_last_n()
    {
        var g = Grid(1,
            new[] { (X, 1u, 0u) },
            new[] { (Y, 1u, 0u) },
            new[] { (0u, 0u, 0u) });
        var lines = CellGridFormatter.Format(g, maxRows: 1, maxCols: 10);
        Assert.Single(lines);
        Assert.Equal("y", lines[0][0].Text);
    }

    [Fact]
    public void Clips_row_to_maxCols()
    {
        var g = Grid(4, new[] { (A, 1u, 0u), (B, 1u, 0u), (C, 1u, 0u), (D, 1u, 0u) });
        var run = Assert.Single(CellGridFormatter.Format(g, 5, maxCols: 2)[0].Runs);
        Assert.Equal("ab", run.Text);
    }

    [Fact]
    public void Strips_c0_controls_keeps_pua()
    {
        // 0x07 (BEL) dropped; 0xE0B0 (powerline) kept.
        var g = Grid(3, new[] { (A, 1u, 0u), (0x07u, 1u, 0u), (0xE0B0u, 1u, 0u) });
        var run = Assert.Single(CellGridFormatter.Format(g, 5, 10)[0].Runs);
        Assert.Equal("a" + char.ConvertFromUtf32(0xE0B0), run.Text);
    }

    [Fact]
    public void Empty_grid_returns_no_lines()
    {
        var g = new CellGrid(System.Array.Empty<Cell>(), 0, 0);
        Assert.Empty(CellGridFormatter.Format(g, 5, 10));
    }
}
