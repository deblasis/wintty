using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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

    public TabOverviewControl() => InitializeComponent();

    public event EventHandler<TabModel>? TabChosen;
    public event EventHandler? Dismissed;

    public void Show(IReadOnlyList<TabModel> tabs, TabModel active)
    {
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

        // Select the active tab and move keyboard focus into the grid so arrow
        // keys, Enter and Esc work immediately without a mouse click.
        if (TilesView.Items.Count > 0)
        {
            TilesView.SelectedIndex = activeIndex;
            TilesView.Focus(FocusState.Programmatic);
        }
    }

    private UIElement BuildTile(TabModel tab, bool isActive)
    {
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
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(icon);
        header.Children.Add(title);

        var paneCount = new TextBlock
        {
            Text = tab.PaneHost.PaneCount > 1 ? $"{tab.PaneHost.PaneCount} panes" : "1 pane",
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var body = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        body.Children.Add(header);
        body.Children.Add(paneCount);

        return new Border
        {
            Width = 200,
            Height = 96,
            Margin = new Thickness(6),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            BorderBrush = isActive
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = body,
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
