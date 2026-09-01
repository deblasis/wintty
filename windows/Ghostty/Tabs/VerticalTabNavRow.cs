using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Expanded-pane row content for a vertical-tab
/// <see cref="NavigationViewItem"/>: title, optional bell, close.
/// </summary>
internal sealed partial class VerticalTabNavRow : Grid
{
    private readonly TextBlock _title;
    private readonly FontIcon _bell;
    private readonly Button _close;
    private Border? _coDragAccent;

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

    /// <summary>The close glyph's button, for the seam's geometry readout.</summary>
    internal FrameworkElement TestSeamCloseButton => _close;

    /// <summary>
    /// Whether the row carries its close button. The compact rail is
    /// icon-only, and MUXC's item template arranges this row's content
    /// past the rail's right edge there, so a close button kept at that
    /// width is laid out outside the pane it belongs to -- invisible
    /// behind the pane's clip, and paying for a button's measure, arrange
    /// and hit-test on every row.
    /// </summary>
    internal bool ShowClose
    {
        get => _close.Visibility == Visibility.Visible;
        set => _close.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The text this row is actually showing. Read by the test seam so a
    /// label assertion sees the rendered TextBlock, not the model property
    /// that was supposed to reach it.
    /// </summary>
    internal string TestSeamRenderedTitle => _title.Text;

    internal void Refresh(TabModel tab)
    {
        _title.Text = tab.EffectiveTitle;
        ToolTipService.SetToolTip(_title, tab.EffectiveTitle);
        _bell.Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed;
        _close.Tag = tab;
    }

    /// <summary>
    /// The 1px left edge a member row wears while its group's header is
    /// dragging the run: the marker that says this row is cargo, not a
    /// drag source of its own. An overlay Border rather than a column, so
    /// the row's layout does not move by a pixel when the gesture starts;
    /// the accent is presentation, and nothing reads it back.
    /// </summary>
    internal void SetCoDragAccent(bool on, Brush accent)
    {
        if (on == (_coDragAccent is not null)) return;
        if (!on)
        {
            Children.Remove(_coDragAccent);
            _coDragAccent = null;
            return;
        }
        _coDragAccent = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = accent,
            IsHitTestVisible = false,
        };
        Children.Add(_coDragAccent);
    }
}
