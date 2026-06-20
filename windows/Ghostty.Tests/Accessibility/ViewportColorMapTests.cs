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

    [Fact]
    public void DifferentColors_ReturnMixed()
    {
        var map = Map("ab", Grid(2, new[] { (A, 1u, 0u), (B, 9u, 0u) }));
        Assert.Equal(ColorResultKind.Mixed, map.Foreground(new TextSpan(0, 2)).Kind);
    }

    [Fact]
    public void RangeCrossingNewline_SkipsDelimiter()
    {
        var map = Map("ab\ncd", Grid(2,
            new[] { (A, 4u, 0u), (B, 4u, 0u) },
            new[] { (C, 4u, 0u), (D, 4u, 0u) }));
        var r = map.Foreground(new TextSpan(0, 5));
        Assert.Equal(ColorResultKind.Uniform, r.Kind);
        Assert.Equal(4u, r.Rgb);
    }

    [Fact]
    public void BlankMiddleLine_StillMapsLaterLine()
    {
        var map = Map("ab\n\ncd", Grid(2,
            new[] { (A, 5u, 0u), (B, 5u, 0u) },
            new[] { (0u, 0u, 0u), (0u, 0u, 0u) },
            new[] { (C, 7u, 0u), (D, 7u, 0u) }));
        var r = map.Foreground(new TextSpan(4, 6));
        Assert.Equal(ColorResultKind.Uniform, r.Kind);
        Assert.Equal(7u, r.Rgb);
    }

    [Fact]
    public void CodepointMismatch_ReturnsNotMapped()
    {
        var map = Map("zzz", Grid(3, new[] { (A, 1u, 0u), (B, 1u, 0u), (C, 1u, 0u) }));
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(0, 3)).Kind);
    }

    [Fact]
    public void WideChar_DeclinesAcrossSpacer()
    {
        var map = Map("中x", Grid(3,
            new[] { (0x4e2du, 1u, 0u), (0u, 1u, 0u), ((uint)'x', 1u, 0u) }));
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(0, 2)).Kind);
    }

    [Fact]
    public void ColumnPastGridWidth_ReturnsNotMapped()
    {
        var map = Map("abcd", Grid(3, new[] { (A, 1u, 0u), (B, 1u, 0u), (C, 1u, 0u) }));
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(0, 4)).Kind);
    }

    [Fact]
    public void EmptyRange_ReturnsNotMapped()
    {
        var map = Map("abc", Grid(3, new[] { (A, 1u, 0u), (B, 1u, 0u), (C, 1u, 0u) }));
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(1, 1)).Kind);
    }

    [Fact]
    public void NewlineOnlyRange_ReturnsNotMapped()
    {
        var map = Map("a\nb", Grid(1,
            new[] { (A, 1u, 0u) },
            new[] { (B, 1u, 0u) }));
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(1, 2)).Kind);
    }

    [Fact]
    public void BlankGrid_ReturnsNotMapped()
    {
        var map = Map("abc", Grid(3, new[] { (0u, 0u, 0u), (0u, 0u, 0u), (0u, 0u, 0u) }));
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(0, 3)).Kind);
    }

    [Fact]
    public void TrailingWhitespaceLine_DoesNotShiftAnchor()
    {
        // The document's last line is whitespace only; the grid trims it as blank.
        // Both anchors must treat trailing whitespace the same way, or the content
        // line shifts off-grid and maps to nothing.
        var map = Map("ab\n  ", Grid(2,
            new[] { (A, 3u, 0u), (B, 3u, 0u) },
            new[] { (0u, 0u, 0u), (0u, 0u, 0u) }));
        var r = map.Foreground(new TextSpan(0, 2)); // "ab"
        Assert.Equal(ColorResultKind.Uniform, r.Kind);
        Assert.Equal(3u, r.Rgb);
    }

    [Fact]
    public void MalformedGrid_LengthMismatch_ReturnsNotMapped()
    {
        // Rows*Cols disagrees with the backing array (e.g. a torn off-thread snapshot):
        // decline instead of indexing out of range.
        var grid = new CellGrid(new[] { new Cell(A, 1u, 0u) }, 5, 5);
        var map = new ViewportColorMap(new TerminalDocument("abc"), grid);
        Assert.Equal(ColorResultKind.NotMapped, map.Foreground(new TextSpan(0, 3)).Kind);
    }
}
