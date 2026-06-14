using System;
using System.Collections.Generic;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Ghostty.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// Full-window grid overview of all tabs. <see cref="Show"/> populates the grid
/// from a snapshot; clicking a tile (or Esc/scrim) raises
/// <see cref="TabChosen"/> / <see cref="Dismissed"/>. No live thumbnails - tiles
/// show the same icon/title/color the tab strip renders.
/// </summary>
internal sealed partial class TabOverviewControl : UserControl
{
    private readonly Dictionary<UIElement, TabModel> _tabByTile = new();
    private FontFamily _previewFont = new("Consolas");
    private Flyout? _hoverFlyout;

    // Tile geometry. Width fixed so grid columns stay uniform; the body is a
    // fixed-size canvas so per-pane pixel rects can be computed at build time
    // (before layout). 3 columns at the 900px-max overview width.
    private const double TileWidth = 280;
    private const double TileBodyWidth = 280;
    private const double TileBodyHeight = 150;

    // Preview text metrics. rows/cols a mini-pane can show are derived from its
    // pixel size and the font size (see BuildPaneContent).
    private const double PreviewFontSize = 11;
    private const double MinPaneSideForText = 30;  // below this, geometry-only
    private const int MaxPreviewRows = 12;

    public TabOverviewControl() => InitializeComponent();

    public event EventHandler<TabModel>? TabChosen;
    public event EventHandler? Dismissed;

    public void Show(IReadOnlyList<TabModel> tabs, TabModel active, string? fontFamily)
    {
        _previewFont = PreviewFont.Resolve(fontFamily);
        TilesView.Items.Clear();
        _tabByTile.Clear();

        var activeIndex = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            var isActive = ReferenceEquals(tabs[i], active);
            if (isActive) activeIndex = i;
            var tile = BuildTile(tabs[i], isActive);
            _tabByTile[tile] = tabs[i];
            TilesView.Items.Add(tile);
        }

        // Select the active tab. Focus is grabbed separately via
        // <see cref="FocusGrid"/> once the hosting popup is actually open -
        // calling Focus here (before the popup opens) silently no-ops because
        // the GridView is not yet in the live visual tree.
        if (TilesView.Items.Count > 0)
            TilesView.SelectedIndex = activeIndex;
    }

    /// <summary>
    /// Move keyboard focus into the grid so arrow keys, Enter and Esc work
    /// without a mouse click. Must be called AFTER the hosting popup is open.
    /// </summary>
    public void FocusGrid()
    {
        if (TilesView.Items.Count > 0)
            TilesView.Focus(FocusState.Programmatic);
    }

    private UIElement BuildTile(TabModel tab, bool isActive)
    {
        // Header: icon + title + pane count on a faint strip.
        var icon = new TabIconPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        icon.Attach(tab.TabIcon);

        var title = new TextBlock
        {
            Text = tab.EffectiveTitle,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };

        var paneCount = new TextBlock
        {
            Text = tab.PaneHost.PaneCount > 1 ? $"{tab.PaneHost.PaneCount} panes" : "1 pane",
            Opacity = 0.7,
            FontSize = (double)Application.Current.Resources["CaptionTextBlockFontSize"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var header = new Grid { Padding = new Thickness(8, 6, 8, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0); Grid.SetColumn(title, 1); Grid.SetColumn(paneCount, 2);
        header.Children.Add(icon); header.Children.Add(title); header.Children.Add(paneCount);
        header.Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"];

        // Body: terminal-dark canvas holding the pane mini-layout.
        var body = new Canvas
        {
            Width = TileBodyWidth,
            Height = TileBodyHeight,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        };
        BuildPaneMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(header);
        stack.Children.Add(body);

        var tile = new Border
        {
            Width = TileWidth,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            BorderBrush = isActive
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
            Child = stack,
        };

        // Hover -> enlarged colored preview of this tab.
        tile.PointerEntered += (_, _) => ShowHoverPreview(tile, tab);
        tile.PointerExited += (_, _) => _hoverFlyout?.Hide();
        return tile;
    }

    // Place one dark mini-pane per leaf on the body Canvas, positioned by the
    // normalized rect from PanePreviewLayout. A 1px inset on each pane lets the
    // body's near-black background read as thin dividers between splits.
    private void BuildPaneMiniLayout(PaneNode root, Canvas body, double fontSize)
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
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x10, 0x18)),
                Child = BuildPaneContent(leaf, w, h, fontSize),
            };
            Canvas.SetLeft(pane, x);
            Canvas.SetTop(pane, y);
            body.Children.Add(pane);
        }
    }

    // The colored preview for one leaf, or a placeholder when blank/too-small/dead.
    private UIElement BuildPaneContent(LeafPane leaf, double w, double h, double fontSize)
    {
        if (w < MinPaneSideForText || h < MinPaneSideForText)
            return new Grid(); // geometry-only: just the dark fill

        var lineHeight = fontSize * 1.36;
        var charWidth = fontSize * 0.6;
        var rows = Math.Min(MaxPreviewRows, (int)((h - 8) / lineHeight));
        var cols = (int)((w - 12) / charWidth);
        if (rows < 1 || cols < 1) return new Grid();

        var handle = SafeSurfaceHandle(leaf);
        var grid = handle == IntPtr.Zero ? (CellGrid?)null : SurfaceCellReader.Read(handle);
        var lines = grid is { } g
            ? CellGridFormatter.Format(g, rows, cols)
            : (IReadOnlyList<PreviewLine>)Array.Empty<PreviewLine>();

        if (lines.Count == 0)
        {
            return new TextBlock
            {
                Text = "—",  // em dash placeholder
                FontFamily = _previewFont,
                FontSize = fontSize,
                Opacity = 0.4,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xAA, 0xB0, 0xC4)),
                Margin = new Thickness(6, 4, 6, 4),
            };
        }

        return BuildLinesView(lines, fontSize);
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
                    FontFamily = _previewFont,
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

    // Show an enlarged colored preview of a tab on hover, anchored to its tile.
    private void ShowHoverPreview(FrameworkElement anchor, TabModel tab)
    {
        const double scale = 1.8;
        var bigBody = new Canvas
        {
            Width = TileBodyWidth * scale,
            Height = TileBodyHeight * scale,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        };
        BuildPaneMiniLayout(tab.PaneHost.RootNode, bigBody, 14);

        _hoverFlyout?.Hide();
        _hoverFlyout = new Flyout
        {
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
                Child = bigBody,
            },
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right,
        };
        _hoverFlyout.ShowAt(anchor);
    }

    // Resolve the leaf's surface handle, or IntPtr.Zero if the leaf isn't wired
    // to a TerminalControl yet (would be a PaneHost bug, but a preview must never
    // crash the overview). A type-pattern check avoids both the null-Tag NRE and
    // the wrong-type cast that leaf.Terminal() would throw.
    private static IntPtr SafeSurfaceHandle(LeafPane leaf)
        => leaf.Tag is Ghostty.Controls.TerminalControl tc ? tc.SurfaceHandle : IntPtr.Zero;

    private void OnTileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is UIElement tile && _tabByTile.TryGetValue(tile, out var tab))
            TabChosen?.Invoke(this, tab);
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        // Only dismiss when the scrim itself (not a tile) is tapped.
        if (ReferenceEquals(e.OriginalSource, Scrim))
            Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                Dismissed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                if (TilesView.SelectedItem is UIElement tile &&
                    _tabByTile.TryGetValue(tile, out var tab))
                {
                    TabChosen?.Invoke(this, tab);
                    e.Handled = true;
                }
                break;
        }
    }
}
