using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// The transient Ctrl+Tab cycle popup. <see cref="Show"/> builds a row of tab
/// tiles -- icon + title over a small colored preview -- and highlights the
/// active one with an accent ring. MainWindow flashes it on each Ctrl+Tab press
/// and auto-dismisses it shortly after.
/// </summary>
internal sealed partial class TabSwitcherPopup : UserControl
{
    private readonly Dictionary<TabModel, Border> _cellByTab = new();

    // Cap each title so a long one can't stretch a tile; it ellipsizes past this.
    private const double TitleMaxWidth = 150;
    private const double PreviewWidth = 150;
    private const double PreviewHeight = 84;
    private const double PreviewFontSize = 9;

    public TabSwitcherPopup() => InitializeComponent();

    public void Show(IReadOnlyList<TabModel> tabs, TabModel active, string? fontFamily)
    {
        CandidateRow.Children.Clear();
        _cellByTab.Clear();

        var renderer = new PanePreviewRenderer(PreviewFont.Resolve(fontFamily));
        foreach (var tab in tabs)
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
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = TitleMaxWidth,
                FontSize = (double)Application.Current.Resources["CaptionTextBlockFontSize"],
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 0, 2, 6),
            };
            header.Children.Add(icon);
            header.Children.Add(title);

            // Slate fill so the 1px per-pane inset reads as dividers between
            // splits (matches the overview); panes paint near-black over it.
            var body = new Canvas
            {
                Width = PreviewWidth,
                Height = PreviewHeight,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3B, 0x43)),
            };
            renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);

            var content = new StackPanel { Orientation = Orientation.Vertical };
            content.Children.Add(header);
            content.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = body });

            var cell = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(6),
                Child = content,
            };
            _cellByTab[tab] = cell;
            CandidateRow.Children.Add(cell);
        }

        Highlight(active);
    }

    public void Highlight(TabModel tab)
    {
        foreach (var (model, cell) in _cellByTab)
        {
            cell.BorderBrush = ReferenceEquals(model, tab)
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        }
    }
}
