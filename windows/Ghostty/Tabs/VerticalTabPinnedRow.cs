using Ghostty.Accessibility;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// A pinned tab in the fixed panel above the scrolling list. Deliberately
/// not a <see cref="NavigationViewItem"/>: the panel must neither scroll
/// nor take part in MUXC selection, so the row is a plain element the
/// strip hosts in its PaneCustomContent.
///
/// One row, two widths. In the expanded pane the row is a body row in
/// everything but scroll and close: icon, title, bell -- the anatomy the
/// pane's scrolling rows wear, at the same 40px pitch, so the pinned
/// cluster reads as ordinary tabs and the boundary line under the last
/// one is what marks the zone. In the compact pane the title column
/// collapses and the row degrades to the icon-only slot that fits 48px;
/// the title rides the tooltip, and the accessible name and the "Pinned"
/// status sit on the row itself, which is the leaf the automation tree
/// sees here.
/// </summary>
internal sealed partial class VerticalTabPinnedRow : Grid
{
    /// <summary>Row height. Fits the 48px compact pane with its inset.</summary>
    internal const double RowHeight = 40;

    /// <summary>The icon slot is a square, the one shape both pane widths agree on.</summary>
    private const double IconSlotSize = 40;

    /// <summary>
    /// Pane width at or above which the title column shows. The compact
    /// pane is 48px wide; the expanded pane 220. Anything at or past this
    /// is wide enough to read a trimmed title.
    /// </summary>
    internal const double TitlePaneWidthThreshold = 96;

    private readonly Grid _iconSlot;
    private readonly FontIcon _bell;
    private readonly TextBlock _title;
    private readonly Grid _textColumn;
    private IconElement? _icon;
    private TextBlock? _iconFallback;
    private bool _showTitle;

    public VerticalTabPinnedRow(TabModel tab, SolidColorBrush accentBrush)
    {
        Tag = tab;
        // Transparent, not null: a null background leaves the row's empty
        // span to hit-test through to the pane, so clicks and presses off
        // the icon would fall through entirely. Body rows hit-test
        // full-bleed through their template; this is that parity.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Height = RowHeight;
        // Same inset the body rows get through NavigationViewItemContentMargin.
        Margin = new Thickness(4, 2, 0, 2);
        // Keyboard parity with the body rows: MUXC's containers are tab
        // stops with their own arrow traversal, and this row is outside
        // MUXC, so it carries the tab stop itself and the strip's key
        // handler moves focus across the boundary. The drop-preview ghost
        // is built from this same class and turns the flag back off.
        IsTabStop = true;
        // Body rows are ListItems because MUXC says so; say it here too, so
        // a client hears one kind of thing on both sides of the boundary.
        AutomationProperties.SetAutomationControlType(
            this, AutomationControlType.ListItem);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconSlotSize) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _iconSlot = new Grid
        {
            Width = IconSlotSize,
            Height = IconSlotSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_iconSlot, 0);
        Children.Add(_iconSlot);

        // Over the icon's corner while the row is icon-only, so the row
        // reads as "this icon is ringing" at either pane width. When the
        // title column shows, the bell moves inline after the title, the
        // way a body row wears it.
        _bell = new FontIcon
        {
            Glyph = "\uEA8F",
            FontSize = 9,
            Foreground = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed,
        };
        _iconSlot.Children.Add(_bell);

        _title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
            Text = tab.EffectiveTitle,
        };
        ToolTipService.SetToolTip(_title, tab.EffectiveTitle);
        // The title column carries title + bell once the row goes wide;
        // the bell itself starts in the icon slot's corner and re-parents
        // in ShowTitle.
        var bellHost = new Grid();
        bellHost.Children.Add(_title);
        _textColumn = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetColumn(_textColumn, 1);
        _textColumn.Children.Add(bellHost);
        Children.Add(_textColumn);

        Refresh(tab);
    }

    /// <summary>
    /// Whether the title column shows. The strip drives this from the pane
    /// width: expanded shows the body-row anatomy, compact collapses back
    /// to the icon-only slot. A no-op when the state did not change, so a
    /// width sweep does not re-parent the bell on every tick.
    /// </summary>
    internal bool ShowTitle
    {
        get => _showTitle;
        set
        {
            if (_showTitle == value) return;
            _showTitle = value;
            _textColumn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            // The bell re-parents between the icon slot's corner (compact)
            // and the title column's trailing edge (expanded).
            if (value)
            {
                _iconSlot.Children.Remove(_bell);
                _bell.HorizontalAlignment = HorizontalAlignment.Right;
                _bell.VerticalAlignment = VerticalAlignment.Center;
                _bell.Margin = new Thickness(0, 0, 4, 0);
                _textColumn.Children.Add(_bell);
            }
            else
            {
                _textColumn.Children.Remove(_bell);
                _bell.HorizontalAlignment = HorizontalAlignment.Right;
                _bell.VerticalAlignment = VerticalAlignment.Bottom;
                _bell.Margin = new Thickness(0);
                _iconSlot.Children.Add(_bell);
            }
        }
    }

    /// <summary>Swap the row's icon for a freshly built one.</summary>
    public void SetIcon(IconElement? icon)
    {
        if (_icon is not null) _iconSlot.Children.Remove(_icon);
        if (_iconFallback is not null) _iconSlot.Children.Remove(_iconFallback);
        _icon = icon;
        _iconFallback = null;

        if (icon is not null)
        {
            // Below the bell (slot 0): the bell badge paints over the icon's
            // corner, and a later child renders on top of an earlier one.
            _iconSlot.Children.Insert(0, icon);
        }
        else
        {
            // A row with nothing in the icon slot is a blank slot: fall back
            // to the title's initial, the same stand-in a collapsed body row
            // reads as when the foreground process has no icon.
            if (Tag is not TabModel tab) return;
            _iconFallback = new TextBlock
            {
                Text = InitialOf(TabAccessibleText.Name(tab)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _iconSlot.Children.Insert(0, _iconFallback);
        }
    }

    /// <summary>
    /// The title's first character, as a whole character: a title opening
    /// with an astral character is a surrogate pair, and halving one draws
    /// U+FFFD where the initial should be.
    /// </summary>
    private static string InitialOf(string name)
    {
        if (name.Length == 0) return "?";
        return char.IsHighSurrogate(name[0]) && name.Length > 1 && char.IsLowSurrogate(name[1])
            ? name[..2]
            : name[..1];
    }

    /// <summary>
    /// The row has to be its own peer for focus to be visible to a client
    /// at all: a plain Grid gets no peer, and without one the keyboard
    /// focus this row takes raises no automation focus event, so a screen
    /// reader never knows the shelf is where focus went.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
        => new VerticalTabPinnedRowAutomationPeer(this);

    /// <summary>
    /// Apply the ink the row draws with: the icon and the title take the
    /// row's foreground (full strength when the selection overlay sits
    /// behind it, muted otherwise) -- the same active/inactive rule a body
    /// row's title follows -- and the bell stays accent.
    /// </summary>
    public void ApplyInk(Brush? foreground)
    {
        if (_icon is not null)
        {
            if (foreground is not null) _icon.Foreground = foreground;
            else _icon.ClearValue(IconElement.ForegroundProperty);
        }
        if (_iconFallback is not null)
        {
            if (foreground is not null) _iconFallback.Foreground = foreground;
            else _iconFallback.ClearValue(TextBlock.ForegroundProperty);
        }
        if (foreground is not null) _title.Foreground = foreground;
        else _title.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>
    /// Everything that follows the tab's title or transient state: the
    /// tooltip, the bell, and the text an assistive client reads.
    /// </summary>
    public void Refresh(TabModel tab)
    {
        ToolTipService.SetToolTip(this, tab.EffectiveTitle);
        _title.Text = tab.EffectiveTitle;
        AutomationProperties.SetName(this, TabAccessibleText.Name(tab));
        AutomationProperties.SetItemStatus(this, TabAccessibleText.Status(tab));
        _bell.Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed;
    }
}
