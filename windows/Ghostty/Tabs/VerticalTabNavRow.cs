using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Expanded-pane row content for a vertical-tab
/// <see cref="NavigationViewItem"/>: title, optional bell and idle moon,
/// close.
/// </summary>
internal sealed partial class VerticalTabNavRow : Grid
{
    // Segoe Fluent / MDL2 "QuietHours" moon. Muted like the horizontal
    // strip's: sleeping is a rest state, not an alert.
    private const string IdleGlyph = "\uE708";
    private const double IdleOpacity = 0.45;

    private readonly TextBlock _title;
    private readonly FontIcon _bell;
    private readonly FontIcon _idle;
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

        // The idle moon: shown while the tab has been untouched
        // (TabIdleTracker), hidden whenever a bell is up -- the bell owns
        // the badge. Muted, not accent, so a resting row does not read
        // as alerting.
        _idle = new FontIcon
        {
            Glyph = IdleGlyph,
            FontSize = 9,
            Foreground = MutedGlyphBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Visibility = IdleBadgeVisible(tab),
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
        // Both badges share the one auto column -- moon first, bell
        // nearest the edge -- so a row carrying both states (idle with a
        // bell on it, between the sweep hiding the moon and the property
        // landing) does not reflow the title.
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badges.Children.Add(_idle);
        badges.Children.Add(_bell);
        Grid.SetColumn(_title, 0);
        Grid.SetColumn(badges, 1);
        textRow.Children.Add(_title);
        textRow.Children.Add(badges);

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
        _idle.Visibility = IdleBadgeVisible(tab);
        // The idle dim rides the title, not the whole row: close stays
        // full-strength so an idle tab still reads as closable.
        _title.Opacity = tab.IsIdle ? IdleOpacity : 1.0;
        _close.Tag = tab;
    }

    /// <summary>
    /// Whether the moon shows right now: idle, and no bell up (the bell
    /// owns the badge, and a ringing tab is never idle anyway -- this
    /// also covers the window where IsIdle has not been recomputed
    /// since the bell landed).
    /// </summary>
    private static Visibility IdleBadgeVisible(TabModel tab)
        => tab.IsIdle && !tab.BellRinging
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Muted brush for the moon: secondary text colour, resolved once at
    /// row construction. A theme flip after construction leaves the moon
    /// in the old theme's secondary ink -- the same exposure the bell's
    /// construction-time accent brush already has in these rows, and the
    /// row rebuilds that follow a pin or a group change re-resolve it.
    /// </summary>
    private static Brush MutedGlyphBrush()
    {
        if (Application.Current.Resources.TryGetValue(
                "TextFillColorSecondaryBrush", out var b) && b is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
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
