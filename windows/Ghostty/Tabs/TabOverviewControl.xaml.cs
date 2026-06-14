using System;
using System.Collections.Generic;
using Ghostty.Branding;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// Full-window tab overview: a Fluent card with a thumbnail grid on the left and
/// an always-on detail rail on the right that shows the selected tab enlarged.
/// Hover sets selection, so the rail tracks both pointer and keyboard. Clicking a
/// tile (or Enter) raises <see cref="TabChosen"/>; Esc / scrim raises
/// <see cref="Dismissed"/>.
/// </summary>
internal sealed partial class TabOverviewControl : UserControl
{
    private readonly Dictionary<UIElement, TabModel> _tabByTile = new();
    private FontFamily _previewFont = new("Consolas");
    private PanePreviewRenderer _renderer = new(new FontFamily("Consolas"));

    // Thumbnail geometry. Body is a fixed-size canvas so per-pane pixel rects can
    // be computed at build time (before layout).
    private const double TileWidth = 216;
    private const double TileBodyWidth = 216;
    private const double TileBodyHeight = 116;
    private const double PreviewFontSize = 11;

    // Detail-rail preview geometry (larger, slightly bigger font).
    private const double RailBodyWidth = 356;
    private const double RailBodyHeight = 232;
    private const double RailFontSize = 13;

    public TabOverviewControl() => InitializeComponent();

    public event EventHandler<TabModel>? TabChosen;
    public event EventHandler? Dismissed;

    public void Show(IReadOnlyList<TabModel> tabs, TabModel active, string? fontFamily)
    {
        _previewFont = PreviewFont.Resolve(fontFamily);
        _renderer = new PanePreviewRenderer(_previewFont);

        BrandLogo.Source = new BitmapImage(AppIconSource.Current);
        CountChip.Text = tabs.Count.ToString();

        TilesView.Items.Clear();
        _tabByTile.Clear();
        DetailRail.Content = null;

        var activeIndex = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (ReferenceEquals(tabs[i], active)) activeIndex = i;
            var tile = BuildTile(tabs[i]);
            _tabByTile[tile] = tabs[i];
            TilesView.Items.Add(tile);
        }

        // Selecting the active tab fires OnSelectionChanged, which paints the rail.
        // Focus is grabbed later via FocusGrid once the hosting popup is open.
        if (TilesView.Items.Count > 0)
            TilesView.SelectedIndex = activeIndex;
    }

    /// <summary>
    /// Move keyboard focus into the grid so arrow keys, Enter and Esc work without
    /// a mouse click. Must be called AFTER the hosting popup is open.
    /// </summary>
    public void FocusGrid()
    {
        if (TilesView.Items.Count > 0)
            TilesView.Focus(FocusState.Programmatic);
    }

    // A thumbnail: header (color dot + icon + title + pane chip) over the dark
    // colored preview body. The GridViewItem container owns the rounded surface
    // and the hover/selected visuals, so the tile itself carries no border state.
    private FrameworkElement BuildTile(TabModel tab)
    {
        var header = BuildHeader(tab, titleFontSize: 13, dotSize: 7, includeChip: true);

        var body = new Canvas
        {
            Width = TileBodyWidth,
            Height = TileBodyHeight,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        };
        _renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Width = TileWidth };
        stack.Children.Add(header);
        stack.Children.Add(body);

        // Hover sets selection; the rail (driven by SelectionChanged) follows.
        stack.PointerEntered += (_, _) => TilesView.SelectedItem = stack;
        return stack;
    }

    // The detail rail: enlarged header + large preview + pane-count line.
    private FrameworkElement BuildDetailRail(TabModel tab)
    {
        var header = BuildHeader(
            tab,
            titleFontSize: (double)Application.Current.Resources["BodyTextBlockFontSize"],
            dotSize: 9,
            includeChip: false);

        var body = new Canvas
        {
            Width = RailBodyWidth,
            Height = RailBodyHeight,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        };
        _renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, RailFontSize);

        var bodyBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Child = body,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var paneCount = new TextBlock
        {
            Text = PaneCountLabel(tab.PaneHost.PaneCount),
            FontSize = (double)Application.Current.Resources["CaptionTextBlockFontSize"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Margin = new Thickness(2, 8, 0, 0),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(header);
        stack.Children.Add(bodyBorder);
        stack.Children.Add(paneCount);
        return stack;
    }

    // Shared header row used by both thumbnails (with chip) and the rail (no chip).
    private static FrameworkElement BuildHeader(TabModel tab, double titleFontSize, double dotSize, bool includeChip)
    {
        var grid = new Grid { Padding = new Thickness(10, 7, 10, 7), ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (includeChip)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = dotSize,
            Height = dotSize,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = tab.Color == TabColor.None ? Visibility.Collapsed : Visibility.Visible,
        };
        if (tab.Color != TabColor.None)
        {
            var c = TabColorPalette.Colors[tab.Color];
            dot.Fill = new SolidColorBrush(Color.FromArgb(0xFF, c.R, c.G, c.B));
        }
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var icon = new TabIconPresenter { VerticalAlignment = VerticalAlignment.Center };
        icon.Attach(tab.TabIcon);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = tab.EffectiveTitle,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = titleFontSize,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);

        if (includeChip)
        {
            var chip = new TextBlock
            {
                Text = PaneCountLabel(tab.PaneHost.PaneCount),
                Opacity = 0.7,
                FontSize = (double)Application.Current.Resources["CaptionTextBlockFontSize"],
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(chip, 3);
            grid.Children.Add(chip);
        }

        return grid;
    }

    private static string PaneCountLabel(int count) => count > 1 ? $"{count} panes" : "1 pane";

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TilesView.SelectedItem is UIElement tile && _tabByTile.TryGetValue(tile, out var tab))
            DetailRail.Content = BuildDetailRail(tab);
    }

    private void OnTileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is UIElement tile && _tabByTile.TryGetValue(tile, out var tab))
            TabChosen?.Invoke(this, tab);
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        // Only dismiss when the scrim itself (not the card) is tapped.
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
