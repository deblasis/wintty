using Ghostty.Core.Accessibility;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class ViewportColorMapTests
{
    // Build a grid from rows of (codepoint, fg, bg) tuples padded to `cols`.
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

    private static ViewportColorMap Map(string text, CellGrid grid) =>
        new(new TerminalDocument(text), grid);

    private const uint A = 'a', B = 'b', C = 'c', D = 'd';

    [Fact]
    public void FreshShell_TopAligned_UniformForeground()
    {
        var map = Map("abc", Grid(3, new[] { (A, 1u, 2u), (B, 1u, 2u), (C, 1u, 2u) }));
        var r = map.Foreground(new TextSpan(0, 3));
        Assert.Equal(ColorResultKind.Uniform, r.Kind);
        Assert.Equal(1u, r.Rgb);
    }

    [Fact]
    public void Background_ReturnsBgChannel()
    {
        var map = Map("abc", Grid(3, new[] { (A, 1u, 2u), (B, 1u, 2u), (C, 1u, 2u) }));
        var r = map.Background(new TextSpan(0, 3));
        Assert.Equal(ColorResultKind.Uniform, r.Kind);
        Assert.Equal(2u, r.Rgb);
    }

    [Fact]
    public void Scrollback_BottomAligned_MapsViewportLine()
    {
        var map = Map("x\nabc", Grid(3, new[] { (A, 7u, 0u), (B, 7u, 0u), (C, 7u, 0u) }));
        var r = map.Foreground(new TextSpan(2, 5));
        Assert.Equal(ColorResultKind.Uniform, r.Kind);
        Assert.Equal(7u, r.Rgb);
    }
}
