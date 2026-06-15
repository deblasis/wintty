using System;
using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Ghostty.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// Renders a tab's pane layout as a scaled, colored, non-interactive preview.
/// Shared by the overview thumbnails and the detail rail so both draw identically.
/// </summary>
internal sealed class PanePreviewRenderer
{
    // Below this pixel side a pane is too small for text; show geometry only.
    private const double MinPaneSideForText = 30;
    private const int MaxPreviewRows = 12;

    private readonly FontFamily _font;

    // Per-leaf snapshot cache. A renderer is created fresh each time the overview
    // opens, so this freezes one snapshot per pane for the overview's lifetime:
    // the thumbnail pass populates it, and every rail rebuild on hover/selection
    // reuses it instead of re-locking and re-copying the live cell buffer. It
    // also keeps the enlarged rail pixel-identical to the thumbnail it came from.
    private readonly Dictionary<LeafPane, CellGrid?> _gridCache = new();

    public PanePreviewRenderer(FontFamily font) => _font = font;

    // Place one dark mini-pane per leaf on the body Canvas, positioned by the
    // normalized rect from PanePreviewLayout. A 1px inset on each pane lets the
    // body fill (a neutral slate, set by the caller) read through as the thin
    // divider between splits.
    public void BuildMiniLayout(PaneNode root, Canvas body, double fontSize)
    {
        var bodyW = body.Width;
        var bodyH = body.Height;
        foreach (var (leaf, rect) in PanePreviewLayout.Compute(root))
        {
            var x = rect.X * bodyW + 1;
            var y = rect.Y * bodyH + 1;
            var w = rect.W * bodyW - 2;
            var h = rect.H * bodyH - 2;
            if (w <= 0 || h <= 0) continue;

            var pane = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0E)),
                Child = BuildPaneContent(leaf, w, h, fontSize),
            };
            Canvas.SetLeft(pane, x);
            Canvas.SetTop(pane, y);
            body.Children.Add(pane);
        }
    }

    // The colored preview for one leaf, or an empty element when blank/too-small/dead.
    private UIElement BuildPaneContent(LeafPane leaf, double w, double h, double fontSize)
    {
        if (w < MinPaneSideForText || h < MinPaneSideForText)
            return new Grid(); // geometry-only: just the dark fill

        var lineHeight = fontSize * 1.36;
        var charWidth = fontSize * 0.6;
        var rows = Math.Min(MaxPreviewRows, (int)((h - 8) / lineHeight));
        var cols = (int)((w - 12) / charWidth);
        if (rows < 1 || cols < 1) return new Grid();

        var grid = ReadGrid(leaf);
        var lines = grid is { } g
            ? CellGridFormatter.Format(g, rows, cols)
            : (IReadOnlyList<PreviewLine>)Array.Empty<PreviewLine>();

        if (lines.Count == 0) return new Grid(); // blank pane: just the dark fill

        return BuildLinesView(lines, fontSize);
    }

    // Read this leaf's full-viewport cell snapshot once, then serve it from cache.
    // Read() returns the whole viewport regardless of display size; the per-pane
    // row/col clamp happens later in CellGridFormatter.Format, so a single cached
    // grid feeds both the small thumbnail and the large rail.
    private CellGrid? ReadGrid(LeafPane leaf)
    {
        if (_gridCache.TryGetValue(leaf, out var cached)) return cached;

        var handle = SafeSurfaceHandle(leaf);
        var grid = handle == IntPtr.Zero ? (CellGrid?)null : SurfaceCellReader.Read(handle);
        _gridCache[leaf] = grid;
        return grid;
    }

    // Render colored preview lines: each line a horizontal row of "chips"
    // (Border painted with the run bg, child TextBlock in the run fg + the
    // configured terminal font so powerline / nerd glyphs render).
    private UIElement BuildLinesView(IReadOnlyList<PreviewLine> lines, double fontSize)
    {
        var col = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4, 3, 4, 3) };
        foreach (var line in lines)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var run in line.Runs)
            {
                var tb = new TextBlock
                {
                    Text = run.Text,
                    FontFamily = _font,
                    FontSize = fontSize,
                    Foreground = new SolidColorBrush(FromRgb(run.Fg)),
                    TextWrapping = TextWrapping.NoWrap,
                    IsTextSelectionEnabled = false,
                };
                rowPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(FromRgb(run.Bg)),
                    Child = tb,
                });
            }
            col.Children.Add(rowPanel);
        }
        return col;
    }

    private static Color FromRgb(uint rgb) => Color.FromArgb(
        0xFF, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    // Resolve the leaf's surface handle, or IntPtr.Zero if the leaf isn't wired
    // to a TerminalControl yet. A type-pattern check avoids both the null-Tag NRE
    // and the wrong-type cast that leaf.Terminal() would throw.
    private static IntPtr SafeSurfaceHandle(LeafPane leaf)
        => leaf.Tag is Ghostty.Controls.TerminalControl tc ? tc.SurfaceHandle : IntPtr.Zero;
}
