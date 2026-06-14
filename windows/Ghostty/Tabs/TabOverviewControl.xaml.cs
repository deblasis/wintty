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
    private PanePreviewRenderer _renderer = new(new FontFamily("Consolas"));

    // The tab the enlarged preview is currently showing (null when hidden).
    // Tracked so a tile-to-tile move doesn't hide-then-show (flicker) and so a
    // repeated PointerEntered on the same tile doesn't rebuild the card.
    private TabModel? _previewTab;

    // Tile geometry. Width fixed so grid columns stay uniform; the body is a
    // fixed-size canvas so per-pane pixel rects can be computed at build time
    // (before layout). 3 columns at the 900px-max overview width.
    private const double TileWidth = 280;
    private const double TileBodyWidth = 280;
    private const double TileBodyHeight = 150;

    // Preview font size for the thumbnail mini-panes.
    private const double PreviewFontSize = 11;

    public TabOverviewControl() => InitializeComponent();

    public event EventHandler<TabModel>? TabChosen;
    public event EventHandler? Dismissed;

    public void Show(IReadOnlyList<TabModel> tabs, TabModel active, string? fontFamily)
    {
        _previewFont = PreviewFont.Resolve(fontFamily);
        _renderer = new PanePreviewRenderer(_previewFont);
        TilesView.Items.Clear();
        _tabByTile.Clear();

        // Clear any preview left over from a previous open.
        _previewTab = null;
        PreviewHost.Child = null;
        PreviewHost.Visibility = Visibility.Collapsed;

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
        _renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);

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
        tile.PointerEntered += (_, _) => ShowHoverPreview(tab);
        tile.PointerExited += (_, _) => RequestHidePreview(tab);
        return tile;
    }

    // Enlarged preview scale relative to a tile body (280x150 -> 504x270).
    private const double HoverScale = 1.8;

    // Show the enlarged colored preview for a tab, centered in the overview.
    // The card lives in PreviewHost (hit-test-invisible), so showing it never
    // perturbs pointer hit-testing on the tiles.
    private void ShowHoverPreview(TabModel tab)
    {
        // Same tab already on screen: nothing to rebuild (avoids re-reading the
        // surface and re-laying-out on every PointerEntered the tile emits).
        if (ReferenceEquals(_previewTab, tab) && PreviewHost.Visibility == Visibility.Visible)
            return;

        _previewTab = tab;
        PreviewHost.Child = BuildEnlargedPreview(tab);
        PreviewHost.Visibility = Visibility.Visible;
    }

    // Hide the preview, but only once the current pointer transition settles.
    // Moving the cursor straight from tile A to tile B fires A.Exited then
    // B.Entered synchronously; B.Entered sets _previewTab=B before this queued
    // callback runs, so we skip the hide and the card never flickers. Leaving
    // the grid entirely leaves _previewTab==tab, so it hides.
    private void RequestHidePreview(TabModel tab)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_previewTab, tab)) return;
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewHost.Child = null;
            _previewTab = null;
        });
    }

    // Build the floating preview card: a title strip over a scaled-up copy of
    // the tile's pane mini-layout. Sized exactly to its content so it needs no
    // scrollbar and is never cropped.
    private UIElement BuildEnlargedPreview(TabModel tab)
    {
        var title = new TextBlock
        {
            Text = tab.EffectiveTitle,
            FontSize = (double)Application.Current.Resources["BodyTextBlockFontSize"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 8, 12, 8),
        };

        var bigBody = new Canvas
        {
            Width = TileBodyWidth * HoverScale,
            Height = TileBodyHeight * HoverScale,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        };
        _renderer.BuildMiniLayout(tab.PaneHost.RootNode, bigBody, PreviewFontSize * HoverScale);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(title);
        stack.Children.Add(bigBody);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
        };
    }

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
