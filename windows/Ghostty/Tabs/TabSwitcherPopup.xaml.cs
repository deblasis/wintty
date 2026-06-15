using System.Collections.Generic;
using Ghostty.Controls;
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

    // Fluent theme resources the tile build loop and Highlight() need, resolved
    // once per Show() instead of re-hitting the resource dictionary for every
    // element of every tile. Re-resolved on each open (not cached at ctor) so a
    // theme switch between openings is honored.
    private SwitcherTheme _theme;

    private readonly record struct SwitcherTheme(
        double CaptionFontSize,
        Brush CardBackground,
        Brush BorderIdle,
        Brush BorderActive);

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
        _theme = ResolveTheme();

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
                FontSize = _theme.CaptionFontSize,
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 0, 2, 6),
            };
            header.Children.Add(icon);
            header.Children.Add(title);

            // Shared slate fill so the 1px per-pane inset reads as dividers
            // between splits (matches the overview); panes paint near-black over it.
            var body = new Canvas
            {
                Width = PreviewWidth,
                Height = PreviewHeight,
                Background = PanePreviewRenderer.DividerFill,
            };
            renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);

            var content = new StackPanel { Orientation = Orientation.Vertical };
            content.Children.Add(header);
            content.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = body });

            var cell = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = _theme.BorderIdle,
                Background = _theme.CardBackground,
                Padding = new Thickness(6),
                Child = content,
            };
            _cellByTab[tab] = cell;
            CandidateRow.Children.Add(cell);
        }

        Highlight(active);
    }

    // Fallbacks mirror the Fluent dark-theme token values; they only apply if a
    // key is missing or mistyped (e.g. an early-init/teardown race), which the
    // AOT-safe ThemeResources.Get swallows instead of throwing mid-open.
    private static SwitcherTheme ResolveTheme() => new(
        CaptionFontSize: ThemeResources.Get("CaptionTextBlockFontSize", 12.0),
        CardBackground: ThemeResources.Get<Brush>("CardBackgroundFillColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF))),
        // SubtleFillColorTransparent is fully transparent white in both themes.
        BorderIdle: ThemeResources.Get<Brush>("SubtleFillColorTransparentBrush",
            new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF))),
        // The selection ring; defaults to the Fluent dark accent (SystemAccentColorLight2).
        BorderActive: ThemeResources.Get<Brush>("AccentFillColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF))));

    private void Highlight(TabModel tab)
    {
        foreach (var (model, cell) in _cellByTab)
        {
            cell.BorderBrush = ReferenceEquals(model, tab)
                ? _theme.BorderActive
                : _theme.BorderIdle;
        }
    }
}
