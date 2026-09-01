using System;
using System.Collections.Generic;
using Ghostty.Controls;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// The transient Ctrl+Tab cycle popup. <see cref="Show"/> builds one card
/// per row of the cycle -- tab tiles (icon + title over a small colored
/// preview) and chip rows for collapsed groups -- and highlights the active
/// one with an accent ring. MainWindow flashes it on each Ctrl+Tab press
/// and auto-dismisses it shortly after.
///
/// The rows come from <see cref="TabStripProjection.HorizontalRows"/>, the
/// same reading the horizontal strip renders: a collapsed group is ONE chip
/// row (color dot + title + member count + chevron), its members suppressed
/// except the active one, which the Edge-135 walk projects as an ordinary
/// tile. The popup never decides visibility itself.
/// </summary>
internal sealed partial class TabSwitcherPopup : UserControl
{
    private readonly Dictionary<TabModel, Border> _cellByTab = new();

    // Idle border per tab rather than one shared brush: a tab carrying a
    // preset color rings itself in that color when it is not the active one.
    private readonly Dictionary<TabModel, Brush> _idleBorderByTab = new();

    // Fluent theme resources the tile build loop and Highlight() need, resolved
    // once per Show() instead of re-hitting the resource dictionary for every
    // element of every tile. Re-resolved on each open (not cached at ctor) so a
    // theme switch between openings is honored.
    private SwitcherTheme _theme;

    // The first tile's preview body, kept for the test seam's rect reporter.
    // The fill it paints is what a pixel oracle measures, and a bare Canvas
    // has no automation peer for a UIA-locating harness to find.
    private FrameworkElement? _firstPreviewBody;

    internal FrameworkElement? TestSeamFirstPreviewBody => _firstPreviewBody;

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

    private const double CellMargin = 4;

    /// <summary>
    /// Outer width of one tile: the preview plus the cell's padding, border
    /// and margin. Drives the column count, so it has to track the values
    /// the build loop actually applies. Chip cells take the same width so
    /// both row kinds share one column math.
    /// </summary>
    private const double CellOuterWidth =
        PreviewWidth + (CellPadding * 2) + (CellBorder * 2) + (CellMargin * 2);

    private const double CellPadding = 6;
    private const double CellBorder = 2;

    /// <summary>
    /// Share of the window the tile grid may take before it starts
    /// scrolling, leaving the card's chrome and a margin visible around it.
    /// </summary>
    private const double MaxGridHeightRatio = 0.7;
    private const double MaxGridWidthRatio = 0.9;

    public TabSwitcherPopup() => InitializeComponent();

    public void Show(TabManager manager, TabModel active, string? fontFamily)
    {
        CandidateRow.Children.Clear();
        _cellByTab.Clear();
        _idleBorderByTab.Clear();
        _firstPreviewBody = null;
        _theme = ResolveTheme();
        // MainWindow assigns Width/Height immediately before calling Show, so
        // ActualWidth still holds the previous open's value (zero the first
        // time). Prefer the explicit size.
        var hostWidth = double.IsNaN(Width) ? ActualWidth : Width;
        var hostHeight = double.IsNaN(Height) ? ActualHeight : Height;
        // Read at call time: a cycle step that expanded a group (a chip
        // activation) is rendered expanded, in step with the strip.
        var rows = TabStripProjection.HorizontalRows(manager);
        CandidateRow.MaximumRowsOrColumns = ColumnsFor(rows.Count, hostWidth);
        if (hostHeight > 0)
            CandidateScroll.MaxHeight = hostHeight * MaxGridHeightRatio;
        // A scroll offset left over from the previous open would show the
        // new grid already scrolled past its first rows.
        CandidateScroll.ChangeView(null, 0, null, disableAnimation: true);

        var renderer = new PanePreviewRenderer(PreviewFont.Resolve(fontFamily));
        foreach (var row in rows)
        {
            switch (row)
            {
                case TabStripProjection.HorizontalRow.Chip { Group: { } group }:
                    CandidateRow.Children.Add(BuildGroupChip(manager, group));
                    break;
                case TabStripProjection.HorizontalRow.Item { Tab: { } tab }:
                    CandidateRow.Children.Add(BuildTabTile(tab, renderer));
                    break;
            }
        }

        Highlight(active);
    }

    /// <summary>
    /// One tab tile: icon + title over the pane preview, ringed by the
    /// active accent when it is the cycle's target.
    /// </summary>
    private Border BuildTabTile(TabModel tab, PanePreviewRenderer renderer)
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
        // between splits (matches the overview); panes paint the terminal
        // background over it.
        var body = new Canvas
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Background = PanePreviewRenderer.DividerFill,
        };
        renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);
        _firstPreviewBody ??= body;

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(header);
        content.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = body });

        // A tab's preset color reaches the preview the same way it
        // reaches the strip: the tint composites over the card fill
        // rather than replacing it, so a colored tile still reads as the
        // same surface as an uncolored one.
        var colored = tab.Color != TabColor.None;
        var idleBorder = colored
            ? TabColorBrush.From(TabColorPalette.Border(tab.Color))
            : _theme.BorderIdle;
        _idleBorderByTab[tab] = idleBorder;

        var cell = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(CellBorder),
            BorderBrush = idleBorder,
            Background = colored
                ? TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: false))
                : _theme.CardBackground,
            Padding = new Thickness(CellPadding),
            Margin = new Thickness(CellMargin),
            Child = content,
        };
        _cellByTab[tab] = cell;
        return cell;
    }

    /// <summary>
    /// One collapsed group as ONE chip row: the strip chip's four-part
    /// anatomy (color dot, title, member count, chevron) on a card of the
    /// tile's outer width, so both row kinds share the wrap grid's column
    /// math. The members render nowhere -- the projection suppressed them
    /// -- and the card is never the highlighted cell: a chip activation
    /// lands on manager truth, never on the chip.
    /// </summary>
    private Border BuildGroupChip(TabManager manager, TabGroup group)
    {
        // The dot paints the opaque preset: the card wash below is the
        // translucent one a tinted tile uses, so a translucent dot would
        // disappear into its own card.
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Background = TabColorBrush.From(TabColorPalette.Border(group.Color)),
        };
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TitleMaxWidth,
            Margin = new Thickness(0, 0, 4, 0),
            Text = group.Title,
        };
        var count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0.7,
            Text = manager.MembersOf(group).Count.ToString(),
        };
        var chevron = new FontIcon
        {
            // FontIcon's default FontFamily is not guaranteed to be the
            // symbol font, so pin it explicitly or the glyph can render
            // as nothing.
            FontFamily = Application.Current.Resources.TryGetValue(
                "SymbolThemeFontFamily", out var ff) && ff is FontFamily fam
                ? fam
                : null,
            Glyph = "\uE76C", // Segoe Fluent / MDL2 "ChevronRight"
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var anatomy = new StackPanel { Orientation = Orientation.Horizontal };
        anatomy.Children.Add(dot);
        anatomy.Children.Add(title);
        anatomy.Children.Add(count);
        anatomy.Children.Add(chevron);

        // Centered in the cell so a short row does not hug the top of a
        // slot the tiles size.
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(anatomy);

        // The group always has a color, so the card always takes the tint:
        // the same translucent wash a tinted tile gets, ringed by the
        // preset's border color as its idle border.
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(CellBorder),
            BorderBrush = TabColorBrush.From(TabColorPalette.Border(group.Color)),
            Background = TabColorBrush.From(
                TabColorPalette.Background(group.Color, selected: false)),
            Padding = new Thickness(CellPadding),
            Margin = new Thickness(CellMargin),
            Width = CellOuterWidth,
            Child = content,
        };
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

    /// <summary>
    /// Column count for <paramref name="count"/> rows: square-ish, so a
    /// handful of tabs still make a compact card, but never wider than the
    /// window can show.
    /// </summary>
    private static int ColumnsFor(int count, double hostWidth)
    {
        if (count <= 1) return 1;
        var square = (int)Math.Ceiling(Math.Sqrt(count));
        var byWidth = hostWidth > 0
            ? (int)((hostWidth * MaxGridWidthRatio) / CellOuterWidth)
            : square;
        return Math.Max(1, Math.Min(square, byWidth));
    }

    private void Highlight(TabModel tab)
    {
        foreach (var (model, cell) in _cellByTab)
        {
            cell.BorderBrush = ReferenceEquals(model, tab)
                ? _theme.BorderActive
                : _idleBorderByTab.TryGetValue(model, out var idle)
                    ? idle
                    : _theme.BorderIdle;
            // Once the grid scrolls, the ring alone cannot tell the user
            // which tile is active if it sits below the fold.
            if (ReferenceEquals(model, tab))
                cell.StartBringIntoView();
        }
    }
}
