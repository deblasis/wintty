using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Row content for a vertical-strip group header: color swatch, title,
/// member count, and the collapse chevron. The owning NavigationViewItem
/// carries the group on its Tag and never selects. Flat by design: the
/// spec's nested sketch cannot render Edge-135, and the chevron is ours
/// for the same reason -- the strip drives it.
/// </summary>
internal sealed partial class VerticalTabGroupHeaderRow : Grid
{
    private readonly Border _swatch;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly FontIcon _chevron;

    public VerticalTabGroupHeaderRow(TabGroup group, int memberCount)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _swatch = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        _title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
            Text = group.Title,
        };

        _count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0.7,
            Text = memberCount.ToString(),
        };

        _chevron = new FontIcon
        {
            FontFamily = Application.Current.Resources.TryGetValue(
                "SymbolThemeFontFamily", out var ff) && ff is FontFamily fam
                ? fam
                : null,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(_swatch, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_count, 2);
        Grid.SetColumn(_chevron, 3);
        Children.Add(_swatch);
        Children.Add(_title);
        Children.Add(_count);
        Children.Add(_chevron);
        // The swatch paints unconditionally: a group has no "no color" state.
        Refresh(group, memberCount);
    }

    /// <summary>
    /// Re-read the group's renderable state; the reconcile pass is the only
    /// caller, so a change lands here once no matter which op moved it.
    /// </summary>
    internal void Refresh(TabGroup group, int memberCount)
    {
        _title.Text = group.Title;
        _count.Text = memberCount.ToString();
        _swatch.Background = TabColorBrush.From(
            TabColorPalette.Background(group.Color, selected: false));
        SetChevron(group.IsCollapsed);
        ToolTipService.SetToolTip(this, group.Title);
    }

    /// <summary>Collapsed points right, expanded points down.</summary>
    private void SetChevron(bool collapsed) =>
        _chevron.Glyph = collapsed ? "\uE76C" : "\uE70D";

    /// <summary>
    /// Title and count ink on the strip's text-contrast answer. The swatch
    /// keeps its own palette fill: the group color is content, not chrome.
    /// </summary>
    internal void ApplyInk(Brush foreground)
    {
        _title.Foreground = foreground;
        _count.Foreground = foreground;
    }
}
