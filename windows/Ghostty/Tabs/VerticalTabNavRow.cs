using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Expanded-pane row content for a vertical-tab
/// <see cref="NavigationViewItem"/>: title, optional bell, close.
/// </summary>
internal sealed class VerticalTabNavRow : Grid
{
    private readonly TextBlock _title;
    private readonly FontIcon _bell;
    private readonly Button _close;

    public VerticalTabNavRow(TabModel tab, SolidColorBrush accentBrush, RoutedEventHandler closeClick)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
            Text = tab.EffectiveTitle,
        };
        ToolTipService.SetToolTip(_title, tab.EffectiveTitle);

        _bell = new FontIcon
        {
            Glyph = "\uEA8F",
            FontSize = 9,
            Foreground = accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed,
        };

        _close = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = tab,
            Content = new FontIcon
            {
                FontFamily = Application.Current.Resources.TryGetValue(
                    "SymbolThemeFontFamily", out var ff) && ff is FontFamily fam
                    ? fam
                    : null,
                Glyph = "\uE894",
                FontSize = 10,
            },
        };
        ToolTipService.SetToolTip(_close, "Close tab");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_close, "Close tab");
        _close.Click += closeClick;

        var textRow = new Grid();
        textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        Grid.SetColumn(_bell, 1);
        textRow.Children.Add(_title);
        textRow.Children.Add(_bell);

        Grid.SetColumn(textRow, 0);
        Grid.SetColumn(_close, 1);
        Children.Add(textRow);
        Children.Add(_close);
    }

    internal void Refresh(TabModel tab)
    {
        _title.Text = tab.EffectiveTitle;
        ToolTipService.SetToolTip(_title, tab.EffectiveTitle);
        _bell.Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed;
        _close.Tag = tab;
    }
}
