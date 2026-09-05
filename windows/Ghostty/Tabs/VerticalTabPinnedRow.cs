using Ghostty.Accessibility;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// A pinned tab, as an icon square in the band above the scrolling list.
/// Deliberately not a <see cref="NavigationViewItem"/>: the band must
/// neither scroll nor take part in MUXC selection, so the square is a
/// plain element the strip hosts in its PaneCustomContent.
///
/// One shape, both pane widths. A pinned tab is an icon square and
/// nothing else -- no title column, no close glyph -- which is what lets
/// <see cref="TabPinBandPanel"/> wrap several of them into one band row
/// where the old rows spent one row each. That change of shape is also
/// what separates the zones: the band and the list are visibly different
/// kinds of thing, so the pinned zone needs no rule drawn under it.
///
/// The title the square gives up rides the tooltip, which the square
/// therefore owes rather than merely offers: two shells of the same kind
/// draw the same icon, and without the tooltip nothing tells them apart.
/// The accessible name and the "Pinned" status sit on the square itself,
/// which is the leaf the automation tree sees here.
/// </summary>
internal sealed partial class VerticalTabPinnedRow : Grid
{
    /// <summary>
    /// The square's edge, from the band's own geometry: the panel
    /// arranges every child at exactly this, and the drop preview
    /// promises a box of exactly this, so all three read one number.
    /// </summary>
    internal const double RowHeight = TabPinBand.ChipSize;

    // Segoe Fluent / MDL2 "QuietHours" moon and the idle dim, matching
    // the body row's pair.
    private const string IdleGlyph = "\uE708";
    private const double IdleOpacity = 0.45;

    private readonly Grid _iconSlot;
    private readonly FontIcon _bell;
    private readonly FontIcon _idle;
    private IconElement? _icon;
    private TextBlock? _iconFallback;

    public VerticalTabPinnedRow(TabModel tab, SolidColorBrush accentBrush)
    {
        Tag = tab;
        // Transparent, not null: a null background leaves the square's
        // empty span to hit-test through to the pane, so clicks and
        // presses off the icon would fall through entirely. Body rows
        // hit-test full-bleed through their template; this is that parity.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Width = RowHeight;
        Height = RowHeight;
        // No margin: the band panel owns every gutter between squares and
        // the strip owns the band's own inset. A margin here would be a
        // second opinion about the pitch, and the two would disagree the
        // moment the band re-columns.
        Margin = new Thickness(0);
        // Keyboard parity with the body rows: MUXC's containers are tab
        // stops with their own arrow traversal, and this square is outside
        // MUXC, so it carries the tab stop itself and the strip's key
        // handler moves focus across the boundary. The drop-preview ghost
        // is built from this same class and turns the flag back off.
        IsTabStop = true;
        // Body rows are ListItems because MUXC says so; say it here too, so
        // a client hears one kind of thing on both sides of the boundary.
        AutomationProperties.SetAutomationControlType(
            this, AutomationControlType.ListItem);

        _iconSlot = new Grid
        {
            Width = RowHeight,
            Height = RowHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_iconSlot);

        // Over the icon's corner: the square reads as "this icon is
        // ringing" without spending width it does not have. There is no
        // inline slot to move to any more -- the square never widens.
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

        // The idle moon over the icon's opposite corner: the square
        // reads as "this icon is asleep" without spending width it does
        // not have, the same trick the bell plays on its corner. The two
        // never show together -- a ringing session is not idle.
        _idle = new FontIcon
        {
            Glyph = IdleGlyph,
            FontSize = 9,
            Foreground = MutedGlyphBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = IdleBadgeVisible(tab),
        };
        _iconSlot.Children.Add(_idle);

        Refresh(tab);
    }

    /// <summary>The icon square, for the seam's geometry readout.</summary>
    internal FrameworkElement TestSeamIconSlot => _iconSlot;

    /// <summary>Swap the square's icon for a freshly built one.</summary>
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
            // A square with nothing in the icon slot is a blank slot: fall
            // back to the title's initial, the same stand-in a collapsed
            // body row reads as when the foreground process has no icon.
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
    /// The square has to be its own peer for focus to be visible to a
    /// client at all: a plain Grid gets no peer, and without one the
    /// keyboard focus this square takes raises no automation focus event,
    /// so a screen reader never knows the band is where focus went.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
        => new VerticalTabPinnedRowAutomationPeer(this);

    /// <summary>
    /// Apply the ink the square draws with: the icon takes the square's
    /// foreground (full strength when the selection overlay sits behind
    /// it, muted otherwise) -- the same active/inactive rule a body row's
    /// title follows -- and the bell stays accent.
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
    }

    /// <summary>
    /// Everything that follows the tab's title or transient state: the
    /// tooltip, the bell, and the text an assistive client reads.
    ///
    /// The tooltip is not decoration here. The square shows an icon and
    /// no title, so it is the only thing that tells two shells of the
    /// same kind apart with a pointer.
    /// </summary>
    public void Refresh(TabModel tab)
    {
        ToolTipService.SetToolTip(this, tab.TooltipText);
        AutomationProperties.SetName(this, TabAccessibleText.Name(tab));
        AutomationProperties.SetItemStatus(this, TabAccessibleText.Status(tab));
        // The initial follows the title too. It was written only by SetIcon,
        // which runs on the ICON changing, so a square whose process
        // resolves no icon kept the letter its title started with when it
        // was pinned -- and on a square that letter is not a stand-in beside
        // the title, it is the only thing drawn.
        if (_iconFallback is not null)
            _iconFallback.Text = InitialOf(TabAccessibleText.Name(tab));
        _bell.Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed;
        _idle.Visibility = IdleBadgeVisible(tab);
        // The whole slot dims -- icon and whatever badge is not showing
        // -- which is the square's only way to whisper "asleep": it has
        // no title to fade. The bell stays undimmed by never coexisting
        // with the idle state.
        _iconSlot.Opacity = tab.IsIdle ? IdleOpacity : 1.0;
    }

    /// <summary>
    /// Whether the moon shows right now: idle, and no bell up (the bell
    /// owns the badge; a ringing session is not idle).
    /// </summary>
    private static Visibility IdleBadgeVisible(TabModel tab)
        => tab.IsIdle && !tab.BellRinging
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Muted brush for the moon: secondary text colour, resolved once at
    /// row construction. A theme flip after construction leaves the moon
    /// in the old theme's secondary ink -- the same exposure the bell's
    /// construction-time accent brush already has in this square.
    /// </summary>
    private static Brush MutedGlyphBrush()
    {
        if (Application.Current.Resources.TryGetValue(
                "TextFillColorSecondaryBrush", out var b) && b is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
