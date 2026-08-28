using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Core;
using Ghostty.Core.Tabs;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// Fluent <see cref="NavigationView"/> vertical tab pane. Replaces the
/// ListView rail + chevron toggle.
/// </summary>
internal sealed partial class VerticalTabStrip : UserControl
{
    private const double RowInsetLeft = 4;
    private const double RowInsetVertical = 2;

    private readonly TabManager _manager;
    private readonly Dictionary<TabModel, NavigationViewItem> _items = new();
    private readonly Dictionary<TabModel, TabHooks> _hooks = new();
    private bool _syncing;
    private bool _shellThemeActive;
    private ElementTheme _elementTheme = ElementTheme.Default;
    private SolidColorBrush? _defaultSelectedTabBgBrush;
    private SolidColorBrush? _selectedTabFillBrush;
    private SolidColorBrush? _shellActiveTextBrush;
    private SolidColorBrush? _shellInactiveTextBrush;
    private SolidColorBrush? _defaultActiveTextBrush;
    private bool _selectionRefreshScheduled;
    private bool _placementSettleHooked;
    private bool _selectionSyncDeferred;
    private uint _stripBackdropPacked = 0x0C0C0C;

    private static readonly SolidColorBrush TransparentBrush =
        new(Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// Per-row subscriptions. Held together so a row teardown cannot
    /// release one and leak the others.
    /// </summary>
    private sealed record TabHooks(
        AotBinding Text,
        AotBinding Color,
        TabIconViewModel IconVm,
        PropertyChangedEventHandler IconHandler)
    {
        public void Dispose()
        {
            Text.Dispose();
            Color.Dispose();
            IconVm.PropertyChanged -= IconHandler;
        }
    }

    /// <summary>Raised when a row close button is clicked.</summary>
    public event Func<TabModel, Task>? CloseRequestedFromRow;

    public double OpenPaneLength
    {
        get => NavView.OpenPaneLength;
        set => NavView.OpenPaneLength = value;
    }

    /// <summary>
    /// Sync MUXC pane mode with the outer strip column width. Terminal
    /// content is external -- never leave NavView in LeftCompact+open.
    /// </summary>
    internal void ApplyPaneLayout(bool expanded, double width)
    {
        NavView.Width = width;
        NavView.MaxWidth = width;
        NavView.OpenPaneLength = width;

        if (expanded)
        {
            // Pane fills the strip column; no content frame beside it.
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            NavView.IsPaneOpen = true;
        }
        else
        {
            NavView.IsPaneOpen = false;
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
            NavView.CompactPaneLength =
                Ghostty.Shell.LayoutCoordinator.VerticalStripCollapsedWidth;
        }

        RefreshSelectionChrome();
    }

    public VerticalTabStrip(TabManager manager)
    {
        InitializeComponent();
        _manager = manager;

        RebuildAllItems();
        SyncSelectionFromManager();

        ApplyNavItemSpacing();
        Canvas.SetZIndex(SelectionRowHost, 0);
        Canvas.SetZIndex(NavView, 1);
        // Deliberately not LayoutUpdated: it fires for every layout pass
        // anywhere in the window, and UpdateSelectionRow allocates a brush
        // per call for colored tabs. SizeChanged plus the explicit refresh
        // on selection/pane changes covers every case that moves the row.
        SizeChanged += (_, _) => UpdateSelectionRow();
        NavView.SizeChanged += (_, _) => UpdateSelectionRow();
        NavView.Loaded += (_, _) => RefreshSelectionChrome();
        Loaded += (_, _) =>
        {
            // Everything SyncSelectionFromManager declined to do while this
            // strip had no template, now that it has one.
            if (_selectionSyncDeferred)
            {
                _selectionSyncDeferred = false;
                SyncSelectionFromManager();
            }

            RefreshSelectionChrome();
        };

        _manager.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _manager.ActiveTabChanged += (_, _) => SyncSelectionFromManager();
    }

    internal SolidColorBrush AccentBrush =>
        Resources.TryGetValue("StripAccentBrush", out var res) && res is SolidColorBrush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);

    /// <summary>
    /// Pane chrome from the terminal palette when window-theme=wintty.
    ///
    /// Everything except the lane's own surface, which is SetChromeFill's:
    /// the palette names its shade, frame-style decides whether it is painted
    /// at all, and only the window has both answers.
    /// </summary>
    internal void ApplyShellChrome(ShellThemeService theme)
    {
        _shellThemeActive = true;
        ApplyTransparentNavPaneSurface();

        // Match horizontal TabHost: accent fill on the selected row.
        var accent = new SolidColorBrush(theme.AccentColor);
        _selectedTabFillBrush = accent;
        HideMuxcSelectedBackground();

        SetNavResource("NavigationViewSelectionIndicatorForeground", TransparentBrush);

        uint accentPacked = PackColor(theme.AccentColor);
        uint activePacked = PackColor(theme.ActiveTabText);
        _shellActiveTextBrush = TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(accentPacked, activePacked));

        RefreshShellInactiveInk();

        ApplySelectedForegroundResources(_shellActiveTextBrush);

        var hoverBg = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
        var pressedBg = ResolveThemeBrush("SubtleFillColorTertiaryBrush");
        SetNavResource("NavigationViewItemBackgroundPointerOver", hoverBg);
        SetNavResource("NavigationViewItemBackgroundPressed", pressedBg);

        RefreshNavViewTheme();
        RecolorNavItems();
        RefreshSelectionChrome();
    }

    /// <summary>
    /// Unselected rows are muted rather than given a second colour, so the
    /// selected row is the only one carrying full-strength ink.
    /// </summary>
    private const byte InactiveInkAlpha = 0xB3;

    /// <summary>
    /// Recalibrate the unselected rows' ink, and the ground the preset tab
    /// colours are mixed against, on the surface the text actually lands on.
    ///
    /// Which is the lane's own fill only while there is one. A frosted or
    /// crystal frame leaves the lane bare so the backdrop shows through, and
    /// the palette's tab-bar shade is then a colour nothing paints: ink
    /// picked against it was measured at 2.37:1 on the shade the strip really
    /// rendered, with the other pole sitting at 4.62:1.
    ///
    /// Scored by ThemeResolution at the ink's own alpha rather than by
    /// PreferLightForeground, because 70% ink is a blend of the pole and the
    /// ground and the pole that wins opaque is not always the pole that wins
    /// blended.
    /// </summary>
    private void RefreshShellInactiveInk()
    {
        if (!_shellThemeActive) return;

        _stripBackdropPacked = _chromeFillRgb ?? _chromeGroundPacked;
        _shellInactiveTextBrush = new SolidColorBrush(
            ThemeResolution.PreferLightForegroundAtAlpha(_stripBackdropPacked, InactiveInkAlpha)
                ? Color.FromArgb(InactiveInkAlpha, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(InactiveInkAlpha, 0x00, 0x00, 0x00));
        ApplyInactiveForegroundResources(_shellInactiveTextBrush);
    }

    /// <summary>
    /// Fluent defaults with opaque pane fill -- no acrylic/light-gray seam.
    /// </summary>
    internal void ApplyDefaultPaneChrome(ElementTheme theme)
    {
        _shellThemeActive = false;
        _elementTheme = theme;
        // Drive the subtree's theme for real. Every {ThemeResource} inside
        // the NavigationView template resolves against this, which is what
        // actually makes the strip honor window-theme.
        RequestedTheme = theme;
        _shellActiveTextBrush = null;
        _shellInactiveTextBrush = null;

        // Bare backdrop, not a Fluent layer. A layer over the backdrop is
        // still a surface trying to separate itself by shade, and on a light
        // desktop with a light palette it cannot: the layer, the backdrop and
        // the terminal landed within a few counts of each other. The
        // boundaries are strokes now (see SetRowSeparator), so the surface
        // itself gets out of the way.
        //
        // Except under High Contrast, which gets no strokes either -- so
        // clearing the surface there leaves the lane with nothing painting it
        // at all. LayerFillColorDefaultBrush is HC-overridable and resolves
        // to a system colour, which is the surface that mode wants.
        //
        // And except when frame-style asks for a solid frame, which is the
        // one case where the strip is meant to be a surface again. That fill
        // is the same one the title row takes, so the two rows stay one
        // piece; the strokes still separate them, because a uniform fill
        // divides them no better than a uniform backdrop did.
        var hc = _highContrast;
        Background = hc
            ? ResolveThemeBrush("LayerFillColorDefaultBrush")
            : _chromeFillRgb is { } chromeFill
                ? TabColorBrush.FromPackedRgb(chromeFill)
                : TransparentBrush;
        _stripBackdropPacked = hc ? PackColor(((SolidColorBrush)Background).Color)
                                  : _chromeGroundPacked;
        ApplyTransparentNavPaneSurface();

        ApplyDefaultSelectedTabResources();

        // Unselected rows sit on the strip, which is a theme surface, so
        // they go back to following the element theme.
        ClearNavResource("NavigationViewItemForeground");
        ClearNavResource("NavigationViewItemForegroundPointerOver");

        // The selected row does not: it is painted with the terminal
        // background, so its title has to keep the brush
        // ApplyDefaultSelectedTabResources just calibrated against that
        // background. Clearing it unconditionally, two lines after applying
        // it, put the title back on the element theme's foreground -- which
        // was survivable only while the terminal was always dark and that
        // foreground was always white. Against the light half of the theme
        // it came out white on #F4F6FB, at 1.11:1.
        if (_defaultActiveTextBrush is null)
        {
            ClearNavResource("NavigationViewItemForegroundSelected");
            ClearNavResource("NavigationViewItemForegroundSelectedPointerOver");
        }

        RefreshNavViewTheme();
        RecolorNavItems();
        RefreshSelectionChrome();
    }

    /// <summary>
    /// Default-path selected row = terminal background, matching horizontal
    /// TabHost.SetSelectedTabColors. Shell theme owns the brushes while active.
    /// </summary>
    internal void SetSelectedTabColors(Windows.UI.Color background, Windows.UI.Color foreground)
    {
        _defaultSelectedTabBgBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, background.R, background.G, background.B));
        _defaultActiveTextBrush = TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(
                PackColor(background), PackColor(foreground)));

        // Deliberately does NOT touch _stripBackdropPacked. That value is the
        // surface the rows sit on, and this is the terminal background, which
        // is what the selected row is filled with -- a different thing. While
        // both wrote it, the winner was whichever ran last, and the order
        // differs between construction and a config reload: inactive titles
        // came up calibrated against the terminal at about 2:1 and flipped to
        // correct the first time the config was touched. SetRowSeparator owns
        // it now.

        if (!_shellThemeActive)
        {
            _selectedTabFillBrush = _defaultSelectedTabBgBrush;
            HideMuxcSelectedBackground();
            SetNavResource("NavigationViewSelectionIndicatorForeground", TransparentBrush);
            ApplySelectedForegroundResources(_defaultActiveTextBrush);
            RefreshNavViewTheme();
        }

        RecolorNavItems();
        RefreshSelectionChrome();
    }

    private void ApplyDefaultSelectedTabResources()
    {
        var hoverBg = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
        var pressedBg = ResolveThemeBrush("SubtleFillColorTertiaryBrush");
        SetNavResource("NavigationViewItemBackgroundPointerOver", hoverBg);
        SetNavResource("NavigationViewItemBackgroundPressed", pressedBg);
        SetNavResource("NavigationViewSelectionIndicatorForeground", TransparentBrush);
        HideMuxcSelectedBackground();

        if (_defaultSelectedTabBgBrush is not null)
        {
            _selectedTabFillBrush = _defaultSelectedTabBgBrush;
            if (_defaultActiveTextBrush is not null)
                ApplySelectedForegroundResources(_defaultActiveTextBrush);
        }
        else
        {
            _selectedTabFillBrush = ResolveThemeBrush("SubtleFillColorTertiaryBrush");
        }
    }

    private void HideMuxcSelectedBackground()
    {
        SetNavResource("NavigationViewItemBackgroundSelected", TransparentBrush);
        SetNavResource("NavigationViewItemBackgroundSelectedPointerOver", TransparentBrush);
        SetNavResource("NavigationViewItemBackgroundSelectedPressed", TransparentBrush);
    }

    /// <summary>
    /// SelectionRow sits on a canvas behind NavView; opaque MUXC pane
    /// fills would hide the custom selected-row overlay.
    /// </summary>
    private void ApplyTransparentNavPaneSurface()
    {
        NavView.Background = TransparentBrush;
        SetNavResource("NavigationViewDefaultPaneBackground", TransparentBrush);
        SetNavResource("NavigationViewExpandedPaneBackground", TransparentBrush);
        SetNavResource("NavigationViewCompactPaneBackground", TransparentBrush);
    }

    private void ApplySelectedForegroundResources(SolidColorBrush selectedFg)
    {
        SetNavResource("NavigationViewItemForegroundSelected", selectedFg);
        SetNavResource("NavigationViewItemForegroundSelectedPointerOver", selectedFg);
    }

    private void ApplyInactiveForegroundResources(SolidColorBrush inactiveFg)
    {
        SetNavResource("NavigationViewItemForeground", inactiveFg);
        SetNavResource("NavigationViewItemForegroundPointerOver", inactiveFg);
    }

    private void ApplyNavItemSpacing()
    {
        var margin = new Thickness(RowInsetLeft, RowInsetVertical, 0, RowInsetVertical);
        NavView.Resources["NavigationViewItemContentMargin"] = margin;
        NavView.Resources["TopNavigationViewItemContentMargin"] = margin;
        NavView.Resources["NavigationViewCompactPanelMargin"] = new Thickness(0);
        NavView.Resources["NavigationViewItemCornerRadius"] = new CornerRadius(0);
    }

    /// <summary>
    /// Paint one straight selected row from the strip inset to the pane edge.
    /// MUXC's rounded pill is hidden; this overlay is the sole selection fill.
    /// </summary>
    /// <summary>
    /// Hide the selected-row fill while the active tab is being morphed
    /// across a layout switch.
    ///
    /// SelectionRow is an overlay on its own canvas rather than part of the
    /// NavigationViewItem, so hiding the item leaves the fill sitting on the
    /// rail -- a colored block still marking a tab that has visibly flown
    /// off to the header.
    /// </summary>
    internal void SetSelectionRowSuppressed(bool suppressed)
    {
        if (_selectionRowSuppressed == suppressed) return;
        _selectionRowSuppressed = suppressed;
        UpdateSelectionRow();
    }

    private bool _selectionRowSuppressed;

    // The surface the strip actually sits on, for the text-contrast maths
    // that used to read the Fluent layer's own colour. Fed from MainWindow,
    // which owns the estimate; a sane default until the first push.
    private uint _chromeGroundPacked = 0x0C0C0C;
    // High Contrast keeps a painted surface on the strip lane; every other
    // path lets the backdrop through. Pushed in rather than detected here so
    // the strip and the window cannot disagree about which mode is live.
    private bool _highContrast;
    // The frame's own fill, or null while the strip is left to the backdrop.
    // Pushed in for the same reason the flag above is: frame-style is a
    // window-level answer and the strip must not re-derive it.
    private uint? _chromeFillRgb;
    private SolidColorBrush? _rowSeparatorBrush;
    private readonly List<Border> _rowSeparators = new();

    /// <summary>
    /// Paint the strip lane, or leave it to the window backdrop.
    ///
    /// Both modes, because the palette path can be asked for a bare lane too:
    /// window-theme names the shade and frame-style decides whether it is
    /// painted. The default path rebuilds the whole pane chrome around the
    /// new surface; the palette path swaps the surface alone, because
    /// window-theme still owns the accent and the foregrounds either way.
    ///
    /// Only the lane's own surface. The selected row keeps its fill from the
    /// terminal in every combination: the seam cover is cut from that fill,
    /// and a translucent one reopens the join with the pane that the row
    /// exists to close.
    /// </summary>
    internal void SetChromeFill(uint? fillRgb)
    {
        if (_chromeFillRgb == fillRgb) return;
        _chromeFillRgb = fillRgb;
        if (_shellThemeActive)
        {
            ApplyShellPaneSurface();
            // The surface the rows' text sits on just changed, and on the
            // palette path this call is where that happens: the window
            // resolves the fill after it hands over the palette, so the ink
            // ApplyShellChrome picked is one frame behind until here.
            RefreshShellInactiveInk();
            RecolorNavItems();
        }
        else ApplyDefaultPaneChrome(_elementTheme);
    }

    /// <summary>
    /// The surface this control resolved for a bare lane under High
    /// Contrast, or null on every other bare lane.
    ///
    /// High Contrast without the palette is the one bare lane that is a
    /// colour at all: the window pushes no fill there precisely because the
    /// surface is an HC-overridable theme resource it cannot name, and this
    /// control resolves it. The host's own two rows read this rather than
    /// resolving again, so all three lane rows come off the one resolution
    /// -- two resolutions of the same resource can disagree about the
    /// element theme they resolve against.
    /// </summary>
    internal Brush? HighContrastLaneSurface => _highContrast ? Background : null;

    /// <summary>
    /// The lane's surface on the palette path.
    ///
    /// No High Contrast arm, unlike the default path: that mode pins the
    /// frame solid at the window, so the fill that arrives here is the
    /// palette's own shade -- which under High Contrast is Windows' colour
    /// already, because the palette is.
    /// </summary>
    private void ApplyShellPaneSurface() =>
        Background = _chromeFillRgb is { } fill
            ? TabColorBrush.FromPackedRgb(fill)
            : TransparentBrush;

    /// <summary>
    /// Colour for the lines between rows, or null to draw none.
    ///
    /// Null is not "invisible": window-theme=wintty and High Contrast paint
    /// their rows from real palettes and separate by shade already, so a
    /// stroke there is a second boundary drawn where there is one edge.
    /// </summary>
    internal void SetRowSeparator(uint? separatorRgb, uint groundRgb, bool highContrast)
    {
        _chromeGroundPacked = groundRgb;
        _highContrast = highContrast;
        _rowSeparatorBrush = separatorRgb is { } rgb
            ? TabColorBrush.FromPackedRgb(rgb)
            : null;
        // The palette path is on the backdrop too whenever the frame is
        // translucent, so it takes the same ground rather than staying on the
        // shade the palette named for a lane that is not being painted.
        if (_shellThemeActive) RefreshShellInactiveInk();
        else _stripBackdropPacked = groundRgb;
        // The lane's own surface depends on the HC flag that just landed.
        if (!_shellThemeActive) ApplyDefaultPaneChrome(_elementTheme);
        UpdateSelectionRow();
        RecolorNavItems();
    }

    /// <summary>
    /// One line in each gap between rows, skipping both gaps that touch the
    /// selected row: those two edges are already drawn, in the accent, by the
    /// selected row's own top and bottom stroke. Drawing them again puts two
    /// lines a pixel apart.
    ///
    /// Rebuilt rather than kept in sync per item, because the thing being
    /// mirrored is MUXC's arranged layout, and the only honest read of that
    /// is to ask every item where it ended up.
    /// </summary>
    private void UpdateRowSeparators(bool selectionRowVisible)
    {
        // Pooled by index rather than rebuilt. This runs from the same
        // refresh the selection row rides, which the constructor keeps off
        // LayoutUpdated specifically so it does not allocate on every layout
        // pass; recreating N-1 Borders per call would put the allocation
        // back by another door.
        var used = 0;

        if (_rowSeparatorBrush is null || ActualWidth <= 0)
        {
            HideSeparatorsFrom(0);
            return;
        }

        var tabs = _manager.Tabs;
        for (var i = 0; i + 1 < tabs.Count; i++)
        {
            // Only skip the gaps the selected row is actually covering. The
            // row is collapsed during a layout morph and on MUXC's first
            // frame, and skipping on those passes left two gaps with nothing
            // drawing them and nothing hiding them either.
            if (selectionRowVisible)
            {
                if (ReferenceEquals(tabs[i], _manager.ActiveTab)) continue;
                if (ReferenceEquals(tabs[i + 1], _manager.ActiveTab)) continue;
            }
            if (!_items.TryGetValue(tabs[i], out var item)) continue;
            if (item.ActualHeight <= 0 || item.ActualWidth <= 0) continue;

            double bottom;
            try
            {
                bottom = item.TransformToVisual(this)
                    .TransformPoint(new Windows.Foundation.Point(0, item.ActualHeight)).Y;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.Runtime.InteropServices.COMException or NullReferenceException)
            {
                // The item is not in the tree yet, or is leaving it. The next
                // refresh places it.
                continue;
            }

            Border line;
            if (used < _rowSeparators.Count)
            {
                line = _rowSeparators[used];
            }
            else
            {
                line = new Border { Height = 1, IsHitTestVisible = false };
                _rowSeparators.Add(line);
                // Below the selected row in paint order, so a row that moves
                // over a line hides it rather than showing it through.
                SelectionRowHost.Children.Insert(0, line);
            }

            line.Width = Math.Max(0, ActualWidth - RowInsetLeft);
            line.Background = _rowSeparatorBrush;
            line.Visibility = Visibility.Visible;
            Canvas.SetLeft(line, RowInsetLeft);
            Canvas.SetTop(line, bottom - RowInsetVertical);
            used++;
        }

        HideSeparatorsFrom(used);
    }

    /// <summary>
    /// Park the pooled lines this pass did not place. Hidden rather than
    /// removed, so closing a tab and opening one does not churn the pool.
    /// </summary>
    private void HideSeparatorsFrom(int index)
    {
        for (var i = index; i < _rowSeparators.Count; i++)
            _rowSeparators[i].Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The filled row behind the selected tab. Exposed so MainWindow can
    /// measure where it ends and cover the pane border for exactly that
    /// span, the way the horizontal strip's seam is covered.
    /// </summary>
    internal FrameworkElement SelectionRowElement => SelectionRow;

    /// <summary>Raised whenever the selection row moves, resizes, or hides.</summary>
    internal event Action? SelectionRowChanged;

    private void UpdateSelectionRow()
    {
        if (_selectionRowSuppressed)
        {
            SelectionRow.Visibility = Visibility.Collapsed;
            UpdateRowSeparators(selectionRowVisible: false);
            SelectionRowChanged?.Invoke();
            return;
        }

        if (_manager.ActiveTab is null
            || !_items.TryGetValue(_manager.ActiveTab, out var item)
            || item.ActualWidth <= 0
            || item.ActualHeight <= 0
            || ActualWidth <= 0)
        {
            SelectionRow.Visibility = Visibility.Collapsed;
            UpdateRowSeparators(selectionRowVisible: false);
            SelectionRowChanged?.Invoke();
            return;
        }

        var topLeft = item.TransformToVisual(this)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var rowHeight = Math.Max(0, item.ActualHeight - RowInsetVertical * 2);
        var rowWidth = Math.Max(0, ActualWidth - RowInsetLeft);

        SelectionRow.Width = rowWidth;
        SelectionRow.Height = rowHeight;
        Canvas.SetLeft(SelectionRow, RowInsetLeft);
        Canvas.SetTop(SelectionRow, topLeft.Y + RowInsetVertical);
        SelectionRow.CornerRadius = new CornerRadius(0);
        SelectionRow.Background = ResolveSelectionRowFill(_manager.ActiveTab);

        // The same folder stroke the horizontal strip gets, rotated: the row
        // meets the pane along its right edge, so that is the side left open
        // and the other three carry the pane's own border colour. A tab with
        // a preset colour is stroked in that colour, matching its pane.
        SelectionRow.BorderBrush = _manager.ActiveTab.Color != TabColor.None
            ? TabColorBrush.From(TabColorPalette.Border(_manager.ActiveTab.Color))
            : AccentBrush;
        SelectionRow.BorderThickness = new Thickness(1, 1, 0, 1);

        SelectionRow.Visibility = Visibility.Visible;
        UpdateRowSeparators(selectionRowVisible: true);
        SelectionRowChanged?.Invoke();
    }

    /// <summary>
    /// Re-place the row once the pane's next layout pass has landed.
    ///
    /// Closing a row above the active one moves the active row without
    /// changing any size this control watches, and MUXC has not re-arranged
    /// the pane by the time the deferred pass runs, so TransformToVisual
    /// still reports the offset the row had before the removal and the fill
    /// is left marking the slot the closed tab vacated. The zero-bounds
    /// retry does not cover it: the surviving item's bounds are non-zero,
    /// only its offset is stale. A bring-into-view scroll moves the row the
    /// same way, for the same reason.
    ///
    /// One-shot rather than the standing LayoutUpdated subscription this
    /// control deliberately avoids: it costs one extra placement per refresh
    /// request instead of one per layout pass anywhere in the window.
    /// </summary>
    private void PlaceSelectionRowAfterLayout()
    {
        if (_placementSettleHooked) return;
        _placementSettleHooked = true;
        LayoutUpdated += OnSelectionRowPlacementSettled;
    }

    private void OnSelectionRowPlacementSettled(object? sender, object e)
    {
        // Unhook first: placing the row invalidates layout, and a handler
        // still attached would be re-entered on the pass that causes.
        LayoutUpdated -= OnSelectionRowPlacementSettled;
        _placementSettleHooked = false;
        UpdateSelectionRow();
    }

    /// <summary>
    /// Fill and readable foreground the active row paints itself with.
    /// The morph ghost stands in for that row across a layout switch, so it
    /// asks for the real chrome rather than approximating it -- an
    /// uncolored tab has a fill too, and a ghost without one reads as the
    /// tab losing its selection for the length of the switch.
    /// </summary>
    internal (SolidColorBrush Fill, SolidColorBrush Foreground) ActiveRowChrome(TabModel tab)
    {
        // The fill always comes from ResolveSelectionRowFill so the ghost and
        // the real row can never disagree on the selected color.
        var fill = ResolveSelectionRowFill(tab);
        if (tab.Color != TabColor.None)
        {
            return (fill, TabColorBrush.FromPackedRgb(TabColorPalette.ForegroundRgb(
                tab.Color, selected: true, _stripBackdropPacked)));
        }

        var rowPacked = PackColor(fill.Color);
        var preferred = _shellActiveTextBrush is not null
            ? PackColor(_shellActiveTextBrush.Color)
            : _defaultActiveTextBrush is not null
                ? PackColor(_defaultActiveTextBrush.Color)
                : rowPacked;
        return (fill, TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(rowPacked, preferred)));
    }

    private SolidColorBrush ResolveSelectionRowFill(TabModel tab)
    {
        if (tab.Color != TabColor.None)
            return TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: true));

        // Mirror horizontal TabHost: shell theme paints accent on the selected
        // handle; default path uses terminal background so the row meets the pane.
        if (_shellThemeActive && _selectedTabFillBrush is not null)
            return _selectedTabFillBrush;
        if (_defaultSelectedTabBgBrush is not null)
            return _defaultSelectedTabBgBrush;
        return _selectedTabFillBrush ?? AccentBrush;
    }

    /// <summary>
    /// Re-apply preset tab colors on every row and the active selection fill.
    /// </summary>
    internal void RefreshTabColors()
    {
        ApplyAllItemTabColors();
        RecolorNavItems();
        RefreshSelectionChrome();
    }

    private void ApplyAllItemTabColors()
    {
        foreach (var (model, item) in _items)
            ApplyItemTabColor(item, model);
    }

    private void ApplyItemTabColor(NavigationViewItem item, TabModel tab)
    {
        var selected = ReferenceEquals(tab, _manager.ActiveTab);
        if (tab.Color != TabColor.None)
        {
            // Active row fill is SelectionRow (full strip width). Item bg
            // only tints inactive rows so we do not double-paint selected.
            if (selected)
                item.ClearValue(Control.BackgroundProperty);
            else
            {
                item.Background = TabColorBrush.From(
                    TabColorPalette.Background(tab.Color, selected: false));
            }
        }
        else
            item.ClearValue(Control.BackgroundProperty);

        // MUXC can ignore NavView-level overrides until item resources are set.
        item.Resources["NavigationViewItemBackgroundSelected"] = TransparentBrush;
        item.Resources["NavigationViewItemBackgroundSelectedPointerOver"] = TransparentBrush;
        item.Resources["NavigationViewItemBackgroundSelectedPressed"] = TransparentBrush;
    }

    private SolidColorBrush ResolveInactiveTextBrush()
    {
        if (_shellInactiveTextBrush is not null)
            return _shellInactiveTextBrush;
        return new SolidColorBrush(
            ThemeResolution.PreferLightForegroundAtAlpha(_stripBackdropPacked, InactiveInkAlpha)
                ? Color.FromArgb(InactiveInkAlpha, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(InactiveInkAlpha, 0x00, 0x00, 0x00));
    }

    private static readonly string[] NavItemForegroundKeys =
    [
        "NavigationViewItemForeground",
        "NavigationViewItemForegroundPointerOver",
        "NavigationViewItemForegroundSelected",
        "NavigationViewItemForegroundSelectedPointerOver",
    ];

    /// <summary>
    /// MUXC template bindings miss some icon-only rows; mirror TabHost's
    /// explicit title recolor for FontIcon glyphs.
    /// </summary>
    private void RecolorNavItems()
    {
        foreach (var (model, item) in _items)
        {
            var active = ReferenceEquals(model, _manager.ActiveTab);
            if (model.Color != TabColor.None)
            {
                var fg = TabColorBrush.FromPackedRgb(
                    TabColorPalette.ForegroundRgb(
                        model.Color, active, _stripBackdropPacked));
                ApplyItemForeground(item, fg, active);
                ApplyItemTabColor(item, model);
                continue;
            }

            if (active)
                ApplyItemForeground(item, ActiveRowChrome(model).Foreground, active: true);
            else
                ApplyItemForeground(item, ResolveInactiveTextBrush(), active: false);

            ApplyItemTabColor(item, model);
        }
    }

    private static void ApplyItemForeground(NavigationViewItem item, Brush? fg, bool active)
    {
        item.ClearValue(NavigationViewItem.ForegroundProperty);
        foreach (var key in NavItemForegroundKeys)
            item.Resources.Remove(key);

        if (fg is not null)
        {
            item.Foreground = fg;
            if (active)
            {
                item.Resources["NavigationViewItemForegroundSelected"] = fg;
                item.Resources["NavigationViewItemForegroundSelectedPointerOver"] = fg;
            }
            else
            {
                item.Resources["NavigationViewItemForeground"] = fg;
                item.Resources["NavigationViewItemForegroundPointerOver"] = fg;
            }
        }

        if (item.Icon is FontIcon fi)
        {
            if (fg is not null)
                fi.Foreground = fg;
            else
                fi.ClearValue(FontIcon.ForegroundProperty);
        }
    }

    /// <summary>
    /// Defer selection-row layout until NavView/item bounds are non-zero.
    /// First vertical load and post-switch refresh share this path.
    /// </summary>
    internal void RefreshSelectionChrome() => ScheduleSelectionLayoutPass(retryIfZeroBounds: true);

    private void ScheduleSelectionLayoutPass(bool retryIfZeroBounds)
    {
        UpdateSelectionRow();
        // Both passes below read offsets from a layout that may not have
        // caught up with the change that asked for this refresh, so the
        // authoritative placement is the one after layout settles.
        PlaceSelectionRowAfterLayout();

        if (_selectionRefreshScheduled) return;
        _selectionRefreshScheduled = true;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _selectionRefreshScheduled = false;
            EnsureActiveItemVisible();
            UpdateSelectionRow();
            RecolorNavItems();

            // MUXC often reports zero item bounds on the first frame after
            // the strip host becomes Visible (horizontal→vertical switch).
            if (!retryIfZeroBounds
                || _manager.ActiveTab is null
                || !_items.TryGetValue(_manager.ActiveTab, out var item)
                || (item.ActualWidth > 0 && item.ActualHeight > 0)
                || ActualWidth <= 0)
            {
                return;
            }

            if (_selectionRefreshScheduled) return;
            _selectionRefreshScheduled = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _selectionRefreshScheduled = false;
                EnsureActiveItemVisible();
                UpdateSelectionRow();
                RecolorNavItems();
            });
        });
    }

    /// <summary>
    /// Keep the manager's active tab selected and scrolled into view.
    /// Required after horizontal→vertical switches while the strip was
    /// Collapsed -- MUXC can drop <see cref="NavigationView.SelectedItem"/>
    /// and leave the active row off-screen in the pane scroller.
    /// </summary>
    /// <remarks>
    /// Does nothing while this strip has never been loaded, which in
    /// horizontal-tab mode is the whole session: the coordinator collapses
    /// this host and deliberately does not prime it, because showing a
    /// never-laid-out NavigationView from the constructor crashed XAML's
    /// measure walk. Assigning SelectedItem is where MUXC resolves the
    /// selected item's container and selection indicator, and on a control
    /// with no template there is nothing to resolve -- which is where an
    /// access violation inside set_SelectedItem has been reported from.
    /// The work is latched and replayed on Loaded instead; every path that
    /// makes this strip visible already calls back in here afterwards, so
    /// the latch only has to cover the case where nothing else does.
    /// </remarks>
    internal void SyncSelectionFromManager()
    {
        if (_syncing) return;

        if (!IsLoaded)
        {
            _selectionSyncDeferred = true;
            return;
        }

        if (_manager.ActiveTab is null) return;
        if (!_items.TryGetValue(_manager.ActiveTab, out var item)) return;

        _syncing = true;
        try { NavView.SelectedItem = item; }
        finally { _syncing = false; }

        ApplyAllItemTabColors();
        RecolorNavItems();
        EnsureActiveItemVisible();
        ScheduleSelectionLayoutPass(retryIfZeroBounds: true);
    }

    // The scroller the rows live inside, out of the NavigationView's
    // template. Null until that template has been applied.
    private FrameworkElement? _menuItemsScroller;

    /// <summary>
    /// Vertical bounds of the scrolling row list, in RootGrid-relative
    /// coordinates via <paramref name="reference"/>, or null while the
    /// template has not been applied.
    /// </summary>
    /// <remarks>
    /// Deliberately the scroller and not this control: with more tabs than
    /// fit, the selected row scrolls out of the list while its layout offset
    /// still reports where it would have been. A caller clipping to the
    /// control instead clips to something that always contains the row, so
    /// the clamp does nothing and a cover gets drawn across the pane at a
    /// height with no tab beside it.
    /// </remarks>
    internal (double Top, double Bottom)? SelectionViewport(UIElement reference)
    {
        _menuItemsScroller ??= FindDescendantByName(NavView, "MenuItemsScrollViewer");
        if (_menuItemsScroller is not { ActualHeight: > 0 } scroller) return null;

        try
        {
            var top = scroller.TransformToVisual(reference)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            return (top, top + scroller.ActualHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return null;
        }
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendantByName(child, name) is { } found) return found;
        }
        return null;
    }

    private void EnsureActiveItemVisible()
    {
        if (_manager.ActiveTab is null) return;
        if (!_items.TryGetValue(_manager.ActiveTab, out var item)) return;

        item.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 0.5,
        });
    }

    /// <summary>
    /// A config reload can land while this strip is still being built or is
    /// already tearing down, and the write into the NavigationView's
    /// resource dictionary fails when it does. Swallowing it costs the
    /// affected brush until the next reload; letting it escape costs far
    /// more, because the caller is one step of several in
    /// OnConfigReloadedChrome and a throw here strands every later step --
    /// which is how the window ended up with a new backdrop over a stale
    /// root background. Same guard PaneHost.ApplyGutterBrushes uses, for
    /// the same reason.
    /// </summary>
    private void SetNavResource(string key, Brush brush)
    {
        try
        {
            NavView.Resources[key] = brush;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                or InvalidOperationException
                                or NullReferenceException)
        {
        }
    }

    private void ClearNavResource(string key)
    {
        try
        {
            NavView.Resources.Remove(key);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                or InvalidOperationException
                                or NullReferenceException)
        {
        }
    }

    private SolidColorBrush ResolveThemeBrush(string key)
    {
        var theme = _elementTheme == ElementTheme.Default
            ? ElementTheme.Dark
            : _elementTheme;

        // Element-scoped first: a FrameworkElement's resource walk honors
        // ThemeDictionaries against its ActualTheme, so this picks up the
        // strip's theme. Application.Current.Resources does NOT -- it
        // always resolves at the app theme, so it is only the fallback.
        if (TryFindBrush(NavView.Resources, key, out var scoped)
            || TryFindBrush(Resources, key, out scoped))
        {
            // Copy so MUXC resource overrides never alias theme-dict brushes.
            return new SolidColorBrush(scoped);
        }

        if (Application.Current.Resources.TryGetValue(key, out var obj)
            && obj is SolidColorBrush src)
        {
            // App-theme'd. Correct whenever the strip theme matches the app
            // theme; the explicit overrides in ApplyShellChrome cover the
            // window-theme-differs case.
            return new SolidColorBrush(src.Color);
        }

        return new SolidColorBrush(
            theme == ElementTheme.Light
                ? Microsoft.UI.Colors.White
                : Microsoft.UI.Colors.Black);
    }

    private static bool TryFindBrush(ResourceDictionary dict, string key, out Color color)
    {
        if (dict.TryGetValue(key, out var obj) && obj is SolidColorBrush b)
        {
            color = b.Color;
            return true;
        }
        color = default;
        return false;
    }

    /// <summary>Force MUXC to re-read overridden pane/item resources.</summary>
    internal void RefreshNavViewTheme()
    {
        var theme = NavView.RequestedTheme;
        NavView.RequestedTheme = theme == ElementTheme.Light
            ? ElementTheme.Dark
            : ElementTheme.Light;
        NavView.RequestedTheme = theme;
    }

    private static uint PackColor(Color c)
        => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private void RebuildAllItems()
    {
        // Remove by what we hold, not by what the manager still has:
        // on a Reset the manager is already empty and rows we own would
        // otherwise stay in MenuItems with their subscriptions live.
        foreach (var tab in _hooks.Keys.ToArray())
            RemoveItem(tab);
        // Row order comes from the projector, the same source the
        // horizontal strip reconciles against, so the two strips cannot
        // disagree by construction. Identical to Tabs while headers are
        // off, which is what makes the swap behavior-neutral; the
        // Add/Remove branches above stay index-honoring because with no
        // headers the collection's indices ARE the projection's.
        foreach (var tab in TabStripProjection.Rows(_manager))
            AddItem(tab);
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // NewStartingIndex matters: TabManager.Move is RemoveAt +
                // Insert, which ObservableCollection reports as Remove then
                // Add, not Move. Appending here would drift the strip order
                // away from the manager on every "Move Tab Left/Right".
                if (e.NewItems is not null)
                {
                    var addIndex = e.NewStartingIndex;
                    foreach (TabModel tab in e.NewItems)
                    {
                        AddItem(tab, addIndex);
                        if (addIndex >= 0) addIndex++;
                    }
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (TabModel tab in e.OldItems)
                        RemoveItem(tab);
                break;
            case NotifyCollectionChangedAction.Reset:
            case NotifyCollectionChangedAction.Move:
                RebuildAllItems();
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                    foreach (TabModel tab in e.OldItems)
                        RemoveItem(tab);
                if (e.NewItems is not null)
                    foreach (TabModel tab in e.NewItems)
                        AddItem(tab);
                break;
        }
        SyncSelectionFromManager();
    }

    private void AddItem(TabModel tab, int index = -1)
    {
        if (_items.ContainsKey(tab)) return;

        var row = new VerticalTabNavRow(tab, AccentBrush, OnRowCloseClick);
        var item = new NavigationViewItem
        {
            Tag = tab,
            Icon = TabIconElementFactory.Create(tab.TabIcon),
            Content = row,
        };
        ApplyItemTitleChrome(item, tab);

        // Title and bell are cheap to reapply, so they share one binding.
        // Color is separate because it triggers a whole-strip recolor, and
        // the icon is separate because its spec lives on TabIconViewModel
        // and changes when the foreground process changes. Folding all
        // three together would re-decode the icon bitmap and recolor every
        // row on every OSC 0/2 title the shell emits.
        var textBinding = AotBinding.Create(tab, _ =>
        {
            if (!_items.TryGetValue(tab, out var navItem)) return;
            if (navItem.Content is VerticalTabNavRow navRow)
                navRow.Refresh(tab);
            ApplyItemTitleChrome(navItem, tab);
        },
        nameof(TabModel.EffectiveTitle),
        nameof(TabModel.ShellReportedTitle),
        nameof(TabModel.UserOverrideTitle),
        nameof(TabModel.BellRinging));

        var colorBinding = AotBinding.Create(tab, _ => RefreshTabColors(),
            nameof(TabModel.Color));

        var vm = tab.TabIcon;
        PropertyChangedEventHandler iconHandler = (_, e) =>
        {
            if (e.PropertyName is not null
                && e.PropertyName != nameof(TabIconViewModel.Icon)
                && e.PropertyName != nameof(TabIconViewModel.IsMdl2Glyph)
                && e.PropertyName != nameof(TabIconViewModel.Mdl2CodePoint))
                return;
            if (_items.TryGetValue(tab, out var navItem))
                navItem.Icon = TabIconElementFactory.Create(tab.TabIcon);
        };
        vm.PropertyChanged += iconHandler;

        _items[tab] = item;
        _hooks[tab] = new TabHooks(textBinding, colorBinding, vm, iconHandler);

        // Fenced because an Insert before the current selection shifts what
        // MUXC considers selected and raises SelectionChanged for a tab the
        // user did not pick. Unfenced, that reaches OnNavSelectionChanged,
        // activates the wrong tab, and comes back around to assign
        // SelectedItem while MUXC is still inside its own notification.
        _syncing = true;
        try
        {
            if (index >= 0 && index <= NavView.MenuItems.Count)
                NavView.MenuItems.Insert(index, item);
            else
                NavView.MenuItems.Add(item);
        }
        finally { _syncing = false; }

        ApplyItemTabColor(item, tab);
    }

    /// <summary>
    /// Everything on the item that follows the tab's title: the hover
    /// tooltip and the text an assistive client reads. The row itself
    /// cannot do this -- it does not know which item holds it, and the
    /// name has to sit on the item, which is the ListItem in the
    /// automation tree.
    /// </summary>
    private static void ApplyItemTitleChrome(NavigationViewItem item, TabModel tab)
    {
        ToolTipService.SetToolTip(item, tab.EffectiveTitle);
        AutomationProperties.SetName(item, TabAccessibleText.Name(tab));
        AutomationProperties.SetItemStatus(item, TabAccessibleText.Status(tab));
    }

    private void OnRowCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabModel tab })
            CloseRequestedFromRow?.Invoke(tab);
    }

    private void RemoveItem(TabModel tab)
    {
        if (!_items.TryGetValue(tab, out var item)) return;

        // Fenced for the same reason as the insert in AddItem: removing the
        // selected row moves MUXC's selection to a neighbour and reports it
        // as the user's choice.
        _syncing = true;
        try { NavView.MenuItems.Remove(item); }
        finally { _syncing = false; }

        _items.Remove(tab);
        if (_hooks.Remove(tab, out var hooks))
            hooks.Dispose();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        if (args.SelectedItem is not NavigationViewItem { Tag: TabModel tab }) return;

        _syncing = true;
        try { _manager.Activate(tab); }
        finally { _syncing = false; }

        ApplyAllItemTabColors();
        RecolorNavItems();
        RefreshSelectionChrome();
    }

    /// <summary>The row rendering <paramref name="tab"/>, if built.</summary>
    internal FrameworkElement? TabElement(TabModel tab)
        => _items.TryGetValue(tab, out var item) ? item : null;

    /// <summary>Resolve TabModel for a nav item hit-test target.</summary>
    internal TabModel? TabFromElement(DependencyObject? source)
    {
        var item = VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source);
        return item?.Tag as TabModel;
    }
}
