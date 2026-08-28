using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Ghostty.Core;
using Ghostty.Core.Tabs;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
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

        BuildPinnedShelf();
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

        HookDragInput();
        Unloaded += (_, _) => CancelDrag("teardown");
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
        // Suppression arriving over a live drag is the layout switch
        // staging: the morph hides this row before anything moves. A drag
        // refuses to start under an existing suppression, so this
        // transition can only be a switch beginning, and the reorder
        // grammar folds first -- the manager reconcile repairs the order,
        // the pane moves second (edge case 9).
        if (suppressed && _drag is not null) CancelDrag("switch");
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

    // The pinned shelf: header, fixed row panel, and the zone's boundary
    // stroke, hosted as NavView.PaneCustomContent. Pinned rows are NOT
    // MenuItems -- they must not scroll and must not take part in MUXC
    // selection -- so they get their own container and their own registry.
    private readonly StackPanel _pinnedShelf = new();
    private readonly TextBlock _pinnedHeader = new();
    private readonly StackPanel _pinnedPanel = new();
    private readonly Border _boundaryStroke = new();
    private readonly Dictionary<TabModel, VerticalTabPinnedRow> _pinnedRows = new();
    private readonly Dictionary<TabModel, TabHooks> _pinnedHooks = new();

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
    /// Height of the pin-boundary stroke. The zone edge is a statement
    /// about the list, not a divider between equals, so it is twice an
    /// ordinary row line and never skippers.
    /// </summary>
    private const double BoundaryStrokeHeight = 2;

    /// <summary>
    /// The pin boundary's stroke. Dimmed while idle; brightened while a
    /// drag is live -- the boundary is the thing a drag-to-pin is aiming
    /// at, and the brightening is the gesture's aiming feedback. Resolved
    /// from the strip accent (a theme resource) on every placement, so
    /// High Contrast re-themes it the way every other accent use here
    /// does; a fresh brush per call because AccentBrush is the shared
    /// resource instance and mutating its alpha would retint the panel.
    /// </summary>
    private Brush BoundaryStrokeBrush()
    {
        var accent = AccentBrush.Color;
        byte alpha = _drag is null ? (byte)0x59 : (byte)0xE6;
        return new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
    }

    /// <summary>
    /// One line in each gap between the scrolling list's rows, skipping both
    /// gaps that touch the selected row: those two edges are already drawn,
    /// in the accent, by the selected row's own top and bottom stroke.
    /// Drawing them again puts two lines a pixel apart.
    ///
    /// Only the list's gaps. The pinned rows live in the fixed panel above
    /// the scroller, so the pin zone's edge is not a gap in this pool any
    /// more: it is the boundary stroke along the shelf's bottom edge
    /// (UpdatePinnedShelfChrome), which starts where the panel ends and the
    /// list begins.
    ///
    /// Rebuilt rather than kept in sync per item, because the thing being
    /// mirrored is MUXC's arranged layout, and the only honest read of that
    /// is to ask every item where it ended up.
    /// </summary>
    private void UpdateRowSeparators(bool selectionRowVisible)
    {
        // The shelf rides this refresh on purpose: it is the pass every
        // selection-placement and drag entry/exit path already calls, so
        // the boundary stroke's brighten/dim never needs a caller of its
        // own and cannot be forgotten on an exit path.
        UpdatePinnedShelfChrome();

        // Pooled by index rather than rebuilt. This runs from the same
        // refresh the selection row rides, which the constructor keeps off
        // LayoutUpdated specifically so it does not allocate on every layout
        // pass; recreating N-1 Borders per call would put the allocation
        // back by another door.
        var used = 0;

        if (ActualWidth <= 0)
        {
            HideSeparatorsFrom(0);
            return;
        }

        var tabs = _manager.Tabs;
        // The pinned prefix renders in the shelf, not in the list, so its
        // slots have no gaps here to draw.
        for (var i = _manager.PinCount; i + 1 < tabs.Count; i++)
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

            // A null row-separator brush means this theme separates by
            // shade and wants no lines at all between rows.
            if (_rowSeparatorBrush is null) continue;

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
            line.Height = 1;
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
        if (_selectionRowSuppressed || (_drag is { HidesSelectionRow: true }))
        {
            SelectionRow.Visibility = Visibility.Collapsed;
            UpdateRowSeparators(selectionRowVisible: false);
            SelectionRowChanged?.Invoke();
            return;
        }

        // Mid-drag the rows' arranged slots run ahead of their visuals
        // (glides ride the compositor), so re-placing the overlay here
        // would paint the accent fill a slot away from the row it marks.
        // The last placement keeps matching what is on screen; the one
        // refresh at the drag's end catches up.
        if (_drag is not null) return;

        if (_manager.ActiveTab is null
            || RowElementOf(_manager.ActiveTab) is not { } item
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

        // Pinned rows carry the same ink rules with no MUXC resources to
        // write: the icon draws in the row's foreground (full strength when
        // the selection overlay sits behind it, muted otherwise), and the
        // bell stays accent, exactly as on a body row.
        foreach (var (model, row) in _pinnedRows)
        {
            var active = ReferenceEquals(model, _manager.ActiveTab);
            row.ApplyInk(model.Color != TabColor.None
                ? TabColorBrush.FromPackedRgb(TabColorPalette.ForegroundRgb(
                    model.Color, active, _stripBackdropPacked))
                : active
                    ? ActiveRowChrome(model).Foreground
                    : ResolveInactiveTextBrush());
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
                || RowElementOf(_manager.ActiveTab) is not { } item
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

        _syncing = true;
        try
        {
            // While the active tab is pinned it has no MenuItems entry at
            // all, and MUXC has nothing that can be selected. Leaving the
            // previous selection standing would keep a body row painted as
            // selected while the strip's active chrome sits on a pinned
            // row, so park the selection at null: the selection overlay is
            // the active chrome for a pinned row and does not consult MUXC.
            NavView.SelectedItem = _items.TryGetValue(_manager.ActiveTab, out var item)
                ? item
                : null;
        }
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
    /// Vertical bounds of the container the active row lives in -- the
    /// scrolling list for a body row, the pinned shelf for a pinned one --
    /// in RootGrid-relative coordinates via <paramref name="reference"/>,
    /// or null while the template has not been applied.
    /// </summary>
    /// <remarks>
    /// Deliberately the scroller and not this control for body rows: with
    /// more tabs than fit, the selected row scrolls out of the list while
    /// its layout offset still reports where it would have been. A caller
    /// clipping to the control instead clips to something that always
    /// contains the row, so the clamp does nothing and a cover gets drawn
    /// across the pane at a height with no tab beside it.
    /// </remarks>
    internal (double Top, double Bottom)? SelectionViewport(UIElement reference)
    {
        _menuItemsScroller ??= FindDescendantByName(NavView, "MenuItemsScrollViewer");

        // Clip to the container the active row actually lives in. A pinned
        // active row sits ABOVE the scroller, so clamping it to the
        // scrolling viewport produces an empty span and the seam cover
        // collapses -- the pane-border join silently disappears for exactly
        // the tabs the panel exists to hold. The shelf is fixed and fully
        // visible, so its bounds are the right clip for its rows.
        var container = _manager.ActiveTab is { } active
                        && RowElementOf(active) is VerticalTabPinnedRow
            ? (FrameworkElement?)_pinnedShelf
            : _menuItemsScroller;
        if (container is not { ActualHeight: > 0 } scroller) return null;

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
        // A drag owns the scroller: a bring-into-view fired by a mid-drag
        // commit would scroll the strip under a still pointer and drag
        // the row off the finger. The next post-drag refresh catches up.
        if (_drag is not null) return;
        if (_manager.ActiveTab is null) return;
        if (!_items.TryGetValue(_manager.ActiveTab, out var item))
        {
            // The active row is in the fixed pinned panel above the
            // scroller: it is always on screen and there is no scroll
            // position to reach it with.
            return;
        }

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

    /// <summary>
    /// The pinned section: a small-caps header, the fixed row panel, and
    /// the zone boundary's stroke along the panel's bottom edge.
    /// </summary>
    /// <remarks>
    /// The shelf rides <see cref="NavigationView.PaneCustomContent"/>
    /// rather than a row of this control's root grid or a MenuItems entry,
    /// and that placement is load-bearing. A root-grid row would sit
    /// OUTSIDE the pane, so it would not collapse with it and would fight
    /// MUXC's own pane/compact layout when the sidebar toggles. A
    /// MenuItems entry scrolls with the list and joins MUXC selection,
    /// which is exactly what a pinned section must not do. PaneCustomContent
    /// is the slot MUXC already reserves inside the pane between the pane
    /// toggle and the scrolling list: it never scrolls, it never selects,
    /// it owns no part of the item template, and it tracks the pane width,
    /// so the 40px icon rows fit both the expanded pane and the 48px
    /// compact one.
    /// </remarks>
    private void BuildPinnedShelf()
    {
        // "Pinned" reads as a section title, not a row: small caps, fixed
        // 24px band, offered only while pins exist (UpdatePinnedShelfChrome),
        // and carrying heading semantics so an assistive client can jump to
        // the section instead of walking into it.
        _pinnedHeader.Text = "Pinned";
        _pinnedHeader.Height = PinnedHeaderHeight;
        _pinnedHeader.FontSize = 12;
        _pinnedHeader.CharacterSpacing = 60;
        _pinnedHeader.Margin = new Thickness(RowInsetLeft + 4, 0, 0, 0);
        _pinnedHeader.VerticalAlignment = VerticalAlignment.Center;
        _pinnedHeader.Visibility = Visibility.Collapsed;
        Microsoft.UI.Xaml.Documents.Typography.SetCapitals(
            _pinnedHeader, Microsoft.UI.Xaml.FontCapitals.SmallCaps);
        AutomationProperties.SetName(_pinnedHeader, "Pinned");
        AutomationProperties.SetHeadingLevel(
            _pinnedHeader, AutomationHeadingLevel.Level2);

        _boundaryStroke.Height = BoundaryStrokeHeight;
        _boundaryStroke.IsHitTestVisible = false;
        _boundaryStroke.Margin = new Thickness(RowInsetLeft, 0, 0, 0);
        _boundaryStroke.Visibility = Visibility.Collapsed;

        _pinnedShelf.Children.Add(_pinnedHeader);
        _pinnedShelf.Children.Add(_pinnedPanel);
        _pinnedShelf.Children.Add(_boundaryStroke);
        _pinnedShelf.Visibility = Visibility.Collapsed;

        NavView.PaneCustomContent = _pinnedShelf;
    }

    /// <summary>Height of the pinned section's header band.</summary>
    private const double PinnedHeaderHeight = 24;

    /// <summary>
    /// The element rendering <paramref name="tab"/>, from whichever
    /// container holds it. Every measurement (row centers, the selection
    /// overlay, the separators) and every drag read resolves rows through
    /// here, so a row's two possible homes differ in membership only: one
    /// coordinate space -- this control's -- serves both, which is what
    /// lets the untouched drag machine keep judging crossings across a
    /// zone change.
    /// </summary>
    private FrameworkElement? RowElementOf(TabModel tab)
        => _pinnedRows.TryGetValue(tab, out var pinned) ? pinned
        : _items.TryGetValue(tab, out var item) ? item
        : null;

    private void RebuildAllItems()
    {
        // Remove by what we hold, not by what the manager still has:
        // on a Reset the manager is already empty and rows we own would
        // otherwise stay in their container with their subscriptions live.
        foreach (var tab in _hooks.Keys.Concat(_pinnedHooks.Keys).ToArray())
            RemoveItem(tab);
        // Row order comes from the projector, the same source the
        // horizontal strip reconciles against, so the two strips cannot
        // disagree by construction. AddItem is membership-only, so adding
        // in projection order lands every row in its container at its slot.
        foreach (var tab in TabStripProjection.Rows(_manager))
            AddItem(tab);
        UpdatePinnedShelfChrome();
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (TabModel tab in e.NewItems)
                        AddItem(tab);
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
        // Membership above, order here. The event index was the order
        // authority while every row was a MenuItems entry (the collection's
        // indices were the projection's), but pinned rows live outside
        // MenuItems, so a manager index counts slots the list does not
        // hold. The projection is the one authority that still speaks for
        // both containers, and ReconcileRowOrder is the pass that applies
        // it -- after the churn of any Move (reported as Remove then Add)
        // and after every other mutation above.
        ReconcileRowOrder();
        SyncSelectionFromManager();
    }

    /// <summary>
    /// Build the row <paramref name="tab"/> renders as, in the container
    /// its pin flag names: the fixed panel for the pinned prefix, the
    /// scrolling list for everything else. Membership only -- where the
    /// row sits among its neighbours is ReconcileRowOrder's answer, taken
    /// from the projection.
    /// </summary>
    private void AddItem(TabModel tab)
    {
        if (_items.ContainsKey(tab) || _pinnedRows.ContainsKey(tab)) return;
        if (tab.IsPinned) AddPinnedRow(tab);
        else AddBodyRow(tab);
    }

    private void AddBodyRow(TabModel tab)
    {
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
        try { NavView.MenuItems.Add(item); }
        finally { _syncing = false; }

        ApplyItemTabColor(item, tab);
    }

    private void AddPinnedRow(TabModel tab)
    {
        var row = new VerticalTabPinnedRow(tab, AccentBrush);
        row.SetIcon(TabIconElementFactory.Create(tab.TabIcon));

        // The same three subscriptions a body row takes, pointed at the
        // pinned row instead: title and bell feed the tooltip and the a11y
        // chrome, color re-inks the whole strip, and the icon rebuilds when
        // the foreground process changes. (AddBodyRow carries the long
        // version of the split rationale.)
        var textBinding = AotBinding.Create(tab, _ =>
        {
            if (_pinnedRows.TryGetValue(tab, out var pinnedRow))
                pinnedRow.Refresh(tab);
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
            if (_pinnedRows.TryGetValue(tab, out var pinnedRow))
                pinnedRow.SetIcon(TabIconElementFactory.Create(tab.TabIcon));
        };
        vm.PropertyChanged += iconHandler;

        _pinnedRows[tab] = row;
        _pinnedHooks[tab] = new TabHooks(textBinding, colorBinding, vm, iconHandler);
        _pinnedPanel.Children.Add(row);
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
        // A removal of the dragged row is either a commit's move phase --
        // the manager re-inserts it right after, which _commitChurn marks --
        // or the row was really closed mid-drag (Ctrl+W). The latter ends
        // the gesture before the container it is following leaves the tree.
        if (_drag is { } drag && ReferenceEquals(tab, drag.Tab) && !_commitChurn)
            CancelDrag("closed");

        // The row lives in exactly one container; take it out of that one.
        // (The panel is not a MUXC selection surface, so its removal needs
        // no fence -- only MenuItems removals can move MUXC's selection.)
        if (_pinnedRows.Remove(tab, out var pinned))
        {
            _pinnedPanel.Children.Remove(pinned);
            if (_pinnedHooks.Remove(tab, out var pinnedHooks))
                pinnedHooks.Dispose();
            return;
        }

        if (!_items.TryGetValue(tab, out var item)) return;

        // Fenced for the same reason as the insert in AddBodyRow: removing
        // the selected row moves MUXC's selection to a neighbour and
        // reports it as the user's choice.
        _syncing = true;
        try { NavView.MenuItems.Remove(item); }
        finally { _syncing = false; }

        _items.Remove(tab);
        if (_hooks.Remove(tab, out var hooks))
            hooks.Dispose();
    }

    /// <summary>
    /// Bring both containers' row order back to the projection's. Rows are
    /// reordered in place -- the element instance each dict holds is moved,
    /// never rebuilt -- so a plain move churns nothing a drag is still
    /// following that the Remove+Add pair did not churn already. A
    /// membership skew (a dict holding a tab its container's projection no
    /// longer names, or counts gone apart) is not something an order pass
    /// can repair; that is what the rebuild is for.
    /// </summary>
    private void ReconcileRowOrder()
    {
        var rows = TabStripProjection.Rows(_manager);
        var pinCount = _manager.PinCount;
        var pinned = rows.Take(pinCount).ToList();
        var body = rows.Skip(pinCount).ToList();

        if (_pinnedRows.Count != pinned.Count || _items.Count != body.Count
            || _pinnedPanel.Children.Count != pinned.Count
            || NavView.MenuItems.Count != body.Count
            || pinned.Any(t => !_pinnedRows.ContainsKey(t))
            || body.Any(t => !_items.ContainsKey(t)))
        {
            RebuildAllItems();
            return;
        }

        for (var i = 0; i < pinned.Count; i++)
        {
            var row = _pinnedRows[pinned[i]];
            if (ReferenceEquals(_pinnedPanel.Children[i], row)) continue;
            var idx = _pinnedPanel.Children.IndexOf(row);
            if (idx >= 0) _pinnedPanel.Children.RemoveAt(idx);
            _pinnedPanel.Children.Insert(i, row);
        }

        var items = NavView.MenuItems;
        _syncing = true;
        try
        {
            for (var i = 0; i < body.Count; i++)
            {
                var row = _items[body[i]];
                if (ReferenceEquals(items[i], row)) continue;
                var idx = items.IndexOf(row);
                if (idx >= 0) items.RemoveAt(idx);
                items.Insert(i, row);
            }
        }
        finally { _syncing = false; }

        UpdatePinnedShelfChrome();
    }

    /// <summary>
    /// The shelf's two state-dependent bits: the header exists only while
    /// pins do, and the boundary stroke marks the edge between the zones,
    /// so it exists only while both do -- the same "both zones exist" gate
    /// the in-list boundary stroke always had.
    /// </summary>
    private void UpdatePinnedShelfChrome()
    {
        var anyPins = _manager.PinCount > 0;
        _pinnedShelf.Visibility = anyPins ? Visibility.Visible : Visibility.Collapsed;
        // The header follows the shelf's gate: BuildPinnedShelf only parks
        // it collapsed as the initial state, so this flip is the one thing
        // that ever makes the 24px headline -- and its heading semantics --
        // render.
        _pinnedHeader.Visibility = anyPins ? Visibility.Visible : Visibility.Collapsed;

        var bothZones = anyPins && _manager.PinCount < _manager.Tabs.Count;
        _boundaryStroke.Visibility = bothZones ? Visibility.Visible : Visibility.Collapsed;
        // Brightens while a drag is live, via BoundaryStrokeBrush's drag
        // gate -- the aiming feedback the drag-to-pin gesture reads.
        if (bothZones) _boundaryStroke.Background = BoundaryStrokeBrush();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        if (args.SelectedItem is not NavigationViewItem { Tag: TabModel tab }) return;

        // A drag never activates (MRU and the active tab are drag-untouched
        // by invariant). MUXC can surface a selection change out of the very
        // press the gesture grew from -- its container lost the pointer to
        // our capture mid-flight -- so a live drag pushes selection back to
        // the manager's active row and leaves activation alone.
        if (_drag is { Machine.Phase: TabDragPhase.Dragging })
        {
            SyncSelectionFromManager();
            return;
        }

        _syncing = true;
        try { _manager.Activate(tab); }
        finally { _syncing = false; }

        ApplyAllItemTabColors();
        RecolorNavItems();
        RefreshSelectionChrome();
    }

    /// <summary>The row rendering <paramref name="tab"/>, if built.</summary>
    internal FrameworkElement? TabElement(TabModel tab) => RowElementOf(tab);

    /// <summary>Resolve TabModel for a nav item hit-test target.</summary>
    internal TabModel? TabFromElement(DependencyObject? source)
    {
        // A row is either a NavigationViewItem in the scrolling list or a
        // VerticalTabPinnedRow in the fixed panel; both carry the tab on Tag.
        if (VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source) is { } item)
            return item.Tag as TabModel;
        return VisualTreeHelperEx.FindAncestor<VerticalTabPinnedRow>(source)?.Tag as TabModel;
    }

    // -----------------------------------------------------------------
    // Drag to reorder (spec 5.2). The strip captures the pointer once a
    // press grows past the start threshold, the row follows through a
    // composition expression, crossings commit through TabManager.Move so
    // the manager is the truth mid-drag, and neighbours glide on
    // composition Translation animations. Rows move INSIDE the strip; the
    // lane itself is never animated (the PR 643 constraint), and a drag
    // never activates a tab or touches MRU.
    // -----------------------------------------------------------------

    /// <summary>
    /// Oracle for the drag harness, the same shape as
    /// LayoutCoordinator's morph trace: the env var is the per-run log
    /// path, so concurrent instances never interleave one file, and the
    /// trace is inert (a null check) when it is unset. Lines pair
    /// DRAG begin/end, one per commit, and the ghosts counts report
    /// composition the strip still believes it is driving -- the oracle
    /// reads any N above zero as a leak, and a `drop` line without a
    /// later `DRAG settle` as a settle that never completed.
    /// </summary>
    private static readonly string? DragTracePath =
        Environment.GetEnvironmentVariable("WINTTY_TABDRAG_TRACE");

    private static void DragTrace(string message)
    {
        if (DragTracePath is null) return;
        try
        {
            System.IO.File.AppendAllText(DragTracePath, message + Environment.NewLine);
        }
        catch
        {
            // A locked or unwritable log must never take the drag down.
        }
    }

    private DragSession? _drag;
    private bool _evalPending;
    // True while a commit's manager mutation is churning the dragged
    // row's own container through RemoveItem/AddItem; distinguishes that
    // churn from a real mid-drag close of the row.
    private bool _commitChurn;

    /// <summary>Everything one drag holds in the air, released by EndDrag.</summary>
    private sealed class DragSession
    {
        public required TabModel Tab;
        public required TabDragReorder Machine;
        public required IReadOnlyList<TabModel> PreDragOrder;
        // Pin flags at gesture start. A cancel restores them before the
        // order replay: the pre-drag order is only expressible with the
        // pre-drag flags set, because Move clamps against the boundary
        // the flags define.
        public required IReadOnlySet<TabModel> PreDragPinned;
        public required uint PointerId;
        // The element the press resolved ownership through. The release's
        // click path needs to know which CONTAINER the press landed in, and
        // by release time a zone churn may already have moved the row: the
        // press-time answer is the one that describes where the user aimed.
        public FrameworkElement? PressRow;
        public double PressY;
        public double PressBaseCenter;
        // The arranged center the anchor currently assumes; the tick's
        // measurement is only believed when it moves off this.
        public double AssumedCenter;
        public double LastPointerY;
        public double AnchorY;
        public double LastScrollOffset;
        public double LastAutoscrollSpeed;
        public double LastAutoscrollMs;
        public bool MotionOn;
        public bool HidesSelectionRow;
        public FrameworkElement Item = null!;
        public ScrollViewer? Scroller;
        public Visual? Visual;
        public CompositionPropertySet? Properties;
        public ExpressionAnimation? Follow;
        // The eased 250ms glide every gap animation rides; created once
        // per drag from the dragged row's compositor.
        public Vector3KeyFrameAnimation? Glide;
        public DispatcherQueueTimer? Autoscroll;
        public TypedEventHandler<DispatcherQueueTimer, object>? AutoscrollTick;
        public EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;
    }

    private void HookDragInput()
    {
        // handledEventsToo: MUXC's containers mark their presses handled
        // for selection, and the drag must see those same presses to arm
        // on them. Nothing here sets Handled on a press or a move, so
        // hover and clicks behave exactly as before a drag existed. The
        // handlers wrap in explicit delegates because AddHandler's
        // parameter is object and a bare method group warns CS8974.
        AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnDragPointerPressed), true);
        AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnDragPointerMoved), true);
        AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnDragPointerReleased), true);
        AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnDragPointerCanceled), true);
        AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnDragPointerCaptureLost), true);
        AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(OnDragKeyDown), true);
    }

    private void OnDragPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null) return;
        // A layout switch stages through SetSelectionRowSuppressed; a
        // drag never starts under one.
        if (_selectionRowSuppressed) return;

        var source = e.OriginalSource as DependencyObject;
        // A row is either a NavigationViewItem in the list or a pinned row
        // in the shelf; both carry the tab on Tag.
        var item = (FrameworkElement?)VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source)
                ?? (FrameworkElement?)VisualTreeHelperEx.FindAncestor<VerticalTabPinnedRow>(source);
        if (item is null || item.Tag is not TabModel tab) return;
        // The close button owns its own presses; no drag grows out of one.
        if (VisualTreeHelperEx.FindAncestor<Button>(source) is not null) return;
        if (RowElementOf(tab) is not { } owned || !ReferenceEquals(owned, item)) return;
        if (_manager.Tabs.Count < 2) return;

        var point = e.GetCurrentPoint(this);
        var machine = new TabDragReorder(_manager.Tabs.Count, _manager.IndexOf(tab));
        machine.Press(point.Position.Y);
        // Each begin..settle pair owns its census: a teardown failure in
        // one drag must not inflate the ghost count of the next.
        _teardownFailures = 0;
        _drag = new DragSession
        {
            Tab = tab,
            Machine = machine,
            PreDragOrder = TabStripProjection.Rows(_manager),
            PreDragPinned = new HashSet<TabModel>(
                _manager.Tabs.Where(t => t.IsPinned)),
            PointerId = e.Pointer.PointerId,
            PressRow = owned,
            LastPointerY = point.Position.Y,
            PressY = point.Position.Y,
        };
    }

    private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId) return;
        var y = e.GetCurrentPoint(this).Position.Y;

        if (drag.Machine.Phase == TabDragPhase.Pressed)
        {
            if (!drag.Machine.Begin(y)) return;
            StartDragVisual(drag, e);
            if (_drag is null) return; // start refused; the click falls through
        }

        drag.LastPointerY = y;
        drag.Machine.SampleVelocity(y, Environment.TickCount64);
        drag.Properties?.InsertVector3("pointer", new Vector3(0, (float)y, 0));
        ScheduleDragEvaluate();
    }

    private void OnDragPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId) return;
        // Velocity first, and signed: Drop clears the sample window, and
        // the remaining travel is slot minus release (AnchorY is the slot
        // the anchor holds), positive = down -- the same axis
        // SampleVelocity feeds. A magnitude here would trip the
        // away-guard and kill every upward settle.
        var velocity = drag.Machine.ReleaseVelocity(drag.AnchorY - drag.LastPointerY);
        var index = drag.Machine.Drop();
        if (index < 0)
        {
            // Never lifted past the threshold: a click. Stand down without
            // touching anything, so activation proceeds exactly as it would
            // have. For a shelf row, "as it would have" is HERE: the pinned
            // panel is outside MUXC selection, so no SelectionChanged will
            // ever carry the click to the manager. The same fenced
            // activation the selection handler runs, for the rows it cannot
            // hear; body clicks keep flowing through MUXC untouched.
            _drag = null;
            if (drag.PressRow is VerticalTabPinnedRow
                && !ReferenceEquals(drag.Tab, _manager.ActiveTab))
            {
                _syncing = true;
                try { _manager.Activate(drag.Tab); }
                finally { _syncing = false; }

                ApplyAllItemTabColors();
                RecolorNavItems();
                RefreshSelectionChrome();
            }
            return;
        }
        // Capture is not released here: a synchronous PointerCaptureLost
        // off our own release would re-enter as a cancel and roll the
        // drop back. The platform releases capture on lift anyway, and
        // by then the session is gone.
        DragTrace($"DRAG drop index={index} velocity={velocity:0}");
        EndDrag(drag, settle: drag.MotionOn, velocity: velocity);
    }

    private void OnDragPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId) return;
        CancelDrag("canceled");
    }

    private void OnDragPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId) return;
        CancelDrag("capture");
    }

    private void OnDragKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_drag is not { Machine.Phase: TabDragPhase.Dragging }) return;
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        CancelDrag("escape");
    }

    private void StartDragVisual(DragSession drag, PointerRoutedEventArgs e)
    {
        if (RowElementOf(drag.Tab) is not { } item) { CancelDrag("closed"); return; }
        if (!CapturePointer(e.Pointer)) { CancelDrag("capture"); return; }

        drag.Item = item;
        drag.MotionOn = TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast);
        drag.HidesSelectionRow = ReferenceEquals(drag.Tab, _manager.ActiveTab);
        drag.Machine.UpdateIndex(_manager.IndexOf(drag.Tab));
        try
        {
            var (center, centers) = MeasureRows(drag.Tab);
            drag.PressBaseCenter = center;
            drag.AssumedCenter = center;
            drag.Machine.UpdateCenters(centers);
        }
        catch (InvalidOperationException)
        {
            // A row the projection holds is not realized; crossings
            // cannot be judged yet, so refuse the lift. The gesture
            // degrades to the click it started as.
            CancelDrag("layout");
            return;
        }

        try
        {
            AttachFollow(drag);
            if (drag.MotionOn)
            {
                AttachLift(drag);
                var glide = drag.Visual!.Compositor.CreateVector3KeyFrameAnimation();
                glide.Duration = TimeSpan.FromMilliseconds(TabStripMotion.GapGlideMs);
                // No key frame at progress 0: the animation starts from
                // whatever the row's Translation holds when it starts,
                // which is the slot delta GlideRow just pinned.
                glide.InsertKeyFrame(1f, Vector3.Zero,
                    drag.Visual.Compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.55f, 0.55f), new Vector2(0f, 1f)));
                drag.Glide = glide;
            }
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            // Composition refused: drop the gesture rather than run one
            // that only pretends to follow the pointer.
            CancelDrag("composition");
            return;
        }

        if (drag.HidesSelectionRow)
            UpdateSelectionRow();
        else
            // The boundary stroke brightens for the length of the gesture
            // regardless of which row is lifted; the active-row case got
            // its refresh above. Placement of the selection row itself
            // stays frozen mid-drag (UpdateSelectionRow's guard).
            UpdateRowSeparators(selectionRowVisible: true);
        StartAutoscroll(drag);
        DragTrace($"DRAG begin index={drag.Machine.Index} " +
            $"rows={_manager.Tabs.Count} motion={(drag.MotionOn ? "on" : "off")}");
    }

    /// <summary>
    /// The follow: the row's composition Translation rides an expression
    /// over a property set the pointer feeds, so the arithmetic runs on
    /// the compositor and a stalled UI thread (the terminal re-rendering
    /// mid-drag) cannot stutter the row. Translation, not Offset: layout
    /// owns Offset, and a commit re-arranges the row mid-drag.
    /// </summary>
    private void AttachFollow(DragSession drag)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(drag.Item, true);
        var visual = ElementCompositionPreview.GetElementVisual(drag.Item);
        var properties = visual.Compositor.CreatePropertySet();
        properties.InsertVector3("pointer", new Vector3(0, (float)drag.LastPointerY, 0));
        ApplyAnchor(drag, properties);
        var follow = visual.Compositor.CreateExpressionAnimation(
            "Vector3(0, P.pointer.y - P.anchor.y, 0)");
        follow.SetReferenceParameter("P", properties);
        visual.StartAnimation("Translation", follow);
        drag.Visual = visual;
        drag.Properties = properties;
        drag.Follow = follow;
    }

    /// <summary>
    /// A commit churns the dragged row's own container (Move surfaces as
    /// Remove+Add and the item is rebuilt), so the follow re-arms on the
    /// fresh visual. The anchor is re-derived from the row's post-commit
    /// slot, so the rebuilt row appears exactly where the pointer had it;
    /// the next tick's measurement confirms.
    /// </summary>
    private void RebindFollow(DragSession drag, double assumedCenter)
    {
        if (RowElementOf(drag.Tab) is not { } item) return;
        drag.Item = item;
        if (!double.IsNaN(assumedCenter)) drag.AssumedCenter = assumedCenter;
        ElementCompositionPreview.SetIsTranslationEnabled(item, true);
        var visual = ElementCompositionPreview.GetElementVisual(item);
        drag.Visual = visual;
        // The churned container scales from its own middle, the same
        // center AttachLift set on the original, or the lift pivots from
        // the corner after the first mid-drag commit.
        visual.CenterPoint = new Vector3(
            (float)item.ActualWidth / 2f, (float)item.ActualHeight / 2f, 0f);
        if (drag.Properties is not null) ApplyAnchor(drag, drag.Properties);
        // Rebuilt at the lift height: re-running the lift spring per
        // crossing would re-bounce the row all the way down the strip.
        if (drag.MotionOn)
            visual.Scale = new Vector3(TabStripMotion.LiftScale, TabStripMotion.LiftScale, 1f);
        if (drag.Follow is not null)
            visual.StartAnimation("Translation", drag.Follow);
    }

    /// <summary>
    /// Re-feed the machine's slot centers from layout, keeping any slot
    /// the strip cannot measure right now at its previous value. Centers
    /// are read in the strip's current frame, so scrolling between here
    /// and the drag's start cannot skew a crossing threshold.
    /// </summary>
    private void RemeasureCenters(DragSession drag)
    {
        var rows = TabStripProjection.Rows(_manager);
        var centers = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var measured = RowCenterY(rows[i]);
            // CenterOf throws for a slot the machine has not grown into
            // yet -- a row added mid-drag, measured before its first
            // arrange, on the very tick this feed expands it. There is
            // no previous belief to keep, so the unresolved measurement
            // stands for this tick; Evaluate treats it as "no crossing"
            // and the next tick re-feeds it.
            centers[i] = !double.IsNaN(measured) || i >= drag.Machine.RowCount
                ? measured
                : drag.Machine.CenterOf(i);
        }
        drag.Machine.UpdateCenters(centers);
    }

    /// <summary>
    /// anchor = pressY - pressBaseCenter + assumedCenter. Translation is
    /// pointer - anchor, which keeps the row's visual position glued to
    /// the pointer through arranged-slot changes (a commit, a scroll):
    /// those shift the assumed center, the anchor shifts with it, and
    /// the visual stays put.
    /// </summary>
    private void ApplyAnchor(DragSession drag, CompositionPropertySet properties)
    {
        drag.AnchorY = drag.PressY - drag.PressBaseCenter + drag.AssumedCenter;
        properties.InsertVector3("anchor", new Vector3(0, (float)drag.AnchorY, 0));
    }

    private void AttachLift(DragSession drag)
    {
        var visual = drag.Visual!;
        visual.CenterPoint = new Vector3(
            (float)drag.Item.ActualWidth / 2f, (float)drag.Item.ActualHeight / 2f, 0f);
        var lift = visual.Compositor.CreateSpringVector3Animation();
        lift.DampingRatio = TabStripMotion.LiftDampingRatio;
        lift.Period = TimeSpan.FromMilliseconds(TabStripMotion.LiftPeriodMs);
        lift.FinalValue = new Vector3(TabStripMotion.LiftScale, TabStripMotion.LiftScale, 1f);
        visual.StartAnimation("Scale", lift);
    }
    // Neighbours currently riding a gap glide, by tab. Doubles as the
    // leak census: anything still in here after the drag's teardown is a
    // ghost the trace reports.
    private readonly Dictionary<TabModel, (FrameworkElement Item, CompositionScopedBatch Batch)>
        _gapMotion = new();
    private int _teardownFailures;

    /// <summary>
    /// Neighbours glide to their new slots. WinUI 3 has no implicit
    /// animations to hang this on, so each commit drives the rows'
    /// composition Translation directly: a row that just changed slots is
    /// offset back by the slot delta and eased home to zero. Attached per
    /// commit and torn down after the drag -- outside a drag the offsets
    /// change only when the strip itself changes, and that must stay
    /// exactly as it was.
    ///
    /// The delta is measured between the pre-commit arranged centers of
    /// the two slots involved, so scroll motion cancels out of it.
    /// </summary>
    private void StartGapGlides(IReadOnlyList<TabModel> beforeRows,
        IReadOnlyList<double> beforeCenters, TabModel dragged)
    {
        if (_drag is not { } drag || !drag.MotionOn) return;
        if (drag.Glide is null || drag.Visual is null) return;

        var afterRows = TabStripProjection.Rows(_manager);
        CompositionScopedBatch? batch = null;
        for (int i = 0; i < afterRows.Count; i++)
        {
            var tab = afterRows[i];
            if (ReferenceEquals(tab, dragged)) continue;
            int old = -1;
            for (int j = 0; j < beforeRows.Count; j++)
                if (ReferenceEquals(beforeRows[j], tab)) { old = j; break; }
            if (old < 0 || old == i) continue;
            if (old >= beforeCenters.Count || i >= beforeCenters.Count) continue;
            double delta = beforeCenters[i] - beforeCenters[old];
            if (double.IsNaN(delta) || Math.Abs(delta) < 0.5) continue;

            batch ??= drag.Visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            GlideRow(tab, delta, batch);
        }
        // Completed lands after the glide's 250ms; only rows still riding
        // THIS batch are handed back, so a re-glide inside the window is
        // not killed by the batch it superseded.
        if (batch is not null)
        {
            var settled = batch;
            settled.Completed += (_, _) =>
            {
                foreach (var entry in _gapMotion.Where(g => ReferenceEquals(g.Value.Batch, settled))
                             .ToList())
                    HandBackRow(entry.Key);
                DragTrace($"DRAG glide ghosts={CountLeakedMotion()}");
            };
            settled.End();
        }
    }

    /// <summary>
    /// One row's glide: pin it visually where its old slot was (Translation
    /// carries the negative delta against the new arrange), then ease to
    /// zero. A row already gliding is stopped first, so it snaps to its
    /// current slot before taking the fresh delta -- chained flicks land
    /// slot-true rather than composing two half-finished glides.
    /// </summary>
    private void GlideRow(TabModel tab, double delta, CompositionScopedBatch batch)
    {
        if (_drag is not { } drag) return;
        if (RowElementOf(tab) is not { } item) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(item);
            visual.StopAnimation("Translation");
            ElementCompositionPreview.SetIsTranslationEnabled(item, true);
            visual.Properties.InsertVector3("Translation", new Vector3(0, (float)-delta, 0));
            if (drag.Glide is not null)
                visual.StartAnimation("Translation", drag.Glide);
            _gapMotion[tab] = (item, batch);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            // Composition refused: this row lands as a cut, like the
            // motion-off path. State never depends on the glide.
        }
    }

    private void HandBackRow(TabModel tab)
    {
        if (!_gapMotion.Remove(tab, out var entry)) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(entry.Item);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            ElementCompositionPreview.SetIsTranslationEnabled(entry.Item, false);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            _teardownFailures++;
        }
    }

    private void DetachGapMotion()
    {
        foreach (var tab in _gapMotion.Keys.ToList())
            HandBackRow(tab);
        _gapMotion.Clear();
    }

    private void StartAutoscroll(DragSession drag)
    {
        // The scroller field only fills when the expanded pane has been
        // opened through SelectionViewport; a first drag on a fresh pane
        // must find it here or autoscroll silently never runs.
        _menuItemsScroller ??= FindDescendantByName(NavView, "MenuItemsScrollViewer");
        var scroller = _menuItemsScroller as ScrollViewer;
        if (scroller is not null)
        {
            drag.Scroller = scroller;
            drag.LastScrollOffset = scroller.VerticalOffset;
            EventHandler<ScrollViewerViewChangedEventArgs> viewChanged =
                (_, _) => OnDragScroll(drag, scroller.VerticalOffset);
            scroller.ViewChanged += viewChanged;
            drag.ViewChanged = viewChanged;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        TypedEventHandler<DispatcherQueueTimer, object> tick = (_, _) => OnAutoscrollTick(drag);
        timer.Tick += tick;
        drag.AutoscrollTick = tick;
        drag.Autoscroll = timer;
        drag.LastAutoscrollMs = Environment.TickCount64;
        timer.Start();
    }

    private void StopAutoscroll(DragSession drag)
    {
        if (drag.Autoscroll is { } timer)
        {
            timer.Stop();
            if (drag.AutoscrollTick is not null) timer.Tick -= drag.AutoscrollTick;
        }
        drag.Autoscroll = null;
        if (drag.Scroller is not null && drag.ViewChanged is not null)
            drag.Scroller.ViewChanged -= drag.ViewChanged;
        drag.Scroller = null;
    }

    /// <summary>
    /// Scrolling moves the content under a still pointer: the arranged
    /// slot shifts by the scroll delta, so the assumed center shifts with
    /// it and the row rides the content instead of being dragged off it.
    /// </summary>
    private void OnDragScroll(DragSession drag, double offset)
    {
        var ds = offset - drag.LastScrollOffset;
        drag.LastScrollOffset = offset;
        if (Math.Abs(ds) < 0.01) return;
        drag.AssumedCenter -= ds;
        if (drag.Properties is not null) ApplyAnchor(drag, drag.Properties);
        RemeasureCenters(drag);
        ScheduleDragEvaluate();
    }

    private void OnAutoscrollTick(DragSession drag)
    {
        var scroller = drag.Scroller;
        if (scroller is null) return;
        try
        {
            var top = scroller.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            // The pinned shelf sits above the scroller, so a pointer over it
            // reads as ABOVE the viewport: to the machine that is fromTop
            // negative, the deepest point of the scroll-up band, and the
            // strip would phantom-scroll up at full speed under a stationary
            // finger resting on the panel. Nothing under the pointer there
            // can reflow -- the shelf is fixed and outside the scroll
            // content -- so the band is defined to start at the scroller's
            // top edge and never above it.
            if (drag.LastPointerY < top)
            {
                if (drag.LastAutoscrollSpeed != 0)
                {
                    drag.LastAutoscrollSpeed = 0;
                    DragTrace("DRAG autoscroll speed=0");
                }
                return;
            }
            var speed = drag.Machine.AutoscrollSpeed(
                drag.LastPointerY, top, top + scroller.ActualHeight);
            if (Math.Abs(speed - drag.LastAutoscrollSpeed) > 0.5)
            {
                drag.LastAutoscrollSpeed = speed;
                DragTrace($"DRAG autoscroll speed={Math.Abs(speed):0}");
            }
            // The 16ms timer interval is a lower bound, not a contract:
            // measure the real delta between ticks so the 360->840 ramp
            // holds its px/s when ticks run late. Clamped so one stalled
            // tick cannot fling the strip.
            var now = Environment.TickCount64;
            var dt = Math.Clamp((now - drag.LastAutoscrollMs) / 1000.0, 0, 0.25);
            drag.LastAutoscrollMs = now;
            if (speed != 0)
                scroller.ChangeView(null, scroller.VerticalOffset + speed * dt, null,
                    disableAnimation: true);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex)) { }
    }

    /// <summary>
    /// Crossings are evaluated on a coalesced dispatcher tick, never per
    /// pointer event: the pointer feeds the property set directly and the
    /// order decisions wait for Normal priority (the drag frame budget).
    /// </summary>
    private void ScheduleDragEvaluate()
    {
        if (_evalPending) return;
        _evalPending = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _evalPending = false;
            EvaluateDrag();
        });
    }

    private void EvaluateDrag()
    {
        if (_drag is not { } drag) return;
        if (!_manager.Tabs.Contains(drag.Tab) || RowElementOf(drag.Tab) is null)
        {
            CancelDrag("closed");
            return;
        }

        drag.Machine.UpdateIndex(_manager.IndexOf(drag.Tab));
        RemeasureCenters(drag);
        double arranged = RowCenterY(drag.Tab);
        if (double.IsNaN(arranged)) return;
        var draggedCenter = arranged + (drag.LastPointerY - drag.AnchorY);

        // Pre-commit arranged centers, one measurement per row: the gap
        // glides read their slot deltas out of this frame, so every delta
        // is scroll-consistent and layout-race-free (arrange cannot run
        // mid-tick).
        var beforeRows = TabStripProjection.Rows(_manager);
        var beforeCenters = new double[beforeRows.Count];
        for (int i = 0; i < beforeRows.Count; i++)
            beforeCenters[i] = RowCenterY(beforeRows[i]);

        var committed = false;
        while (drag.Machine.Evaluate(draggedCenter) is { } crossing)
        {
            var from = _manager.IndexOf(drag.Tab);
            // The pinned prefix is a slot range to the machine; a crossing
            // that lands on the far side of it is a zone change Move alone
            // would clamp away. SetPinned first relocates the row to the
            // boundary (end of the prefix pinning down, first unpinned slot
            // pinning up), and the Move then places it at the crossing's
            // slot inside the new zone -- the drop position, not append-last.
            var zone = TabPinBoundary.Classify(
                drag.Tab.IsPinned, _manager.PinCount, _manager.Tabs.Count, crossing.To);
            _commitChurn = true;
            try
            {
                if (zone.Op != TabPinZoneOp.None)
                {
                    bool pin = zone.Op == TabPinZoneOp.Pin;
                    _manager.SetPinned(drag.Tab, pin);
                    DragTrace($"DRAG {(pin ? "pin" : "unpin")} {crossing.From}->{crossing.To}");
                    from = _manager.IndexOf(drag.Tab);
                    if (from < 0) { CancelDrag("closed"); return; }
                }
                _manager.Move(from, crossing.To);
            }
            finally { _commitChurn = false; }
            if (zone.Op != TabPinZoneOp.None)
            {
                // The boundary is the thing this gesture aims at, so the
                // one commit that moves it repaints it now rather than
                // leaving the stroke at its pre-crossing gap until the
                // drag ends. Ordinary moves keep the deliberate mid-drag
                // freeze: nothing about the boundary changed for them.
                if (drag.HidesSelectionRow) UpdateSelectionRow();
                else UpdateRowSeparators(selectionRowVisible: true);
            }
            if (RowElementOf(drag.Tab) is null) { CancelDrag("closed"); return; }

            // Move clamps at the pin boundary and no-ops on collapse, so
            // read the truth back: a crossing that did not land must not
            // re-anchor the row to a slot it never reached -- the next
            // tick would re-cross it for the rest of the gesture. Tell
            // the machine where the row actually is, skip the rebind,
            // and break: re-evaluating now would re-fire the identical
            // refused crossing.
            var actual = _manager.IndexOf(drag.Tab);
            if (actual != crossing.To)
            {
                drag.Machine.UpdateIndex(actual);
                DragTrace($"DRAG refused {crossing.From}->{crossing.To}");
                break;
            }
            RebindFollow(drag, beforeCenters[crossing.To]);
            committed = true;
            DragTrace($"DRAG commit {crossing.From}->{crossing.To}");
        }

        StartGapGlides(beforeRows, beforeCenters, drag.Tab);

        // On a tick with no commit, a measurement that has drifted off the
        // anchor's assumption is layout truth catching up (a row height
        // change, a pane resize) and is adopted. After a commit the stale
        // pre-arrange measurement is exactly what the anchor already
        // modeled, and believing it would throw the row back a slot.
        if (!committed)
        {
            double measured = RowCenterY(drag.Tab);
            if (!double.IsNaN(measured) && Math.Abs(measured - drag.AssumedCenter) > 0.5)
            {
                drag.AssumedCenter = measured;
                if (drag.Properties is not null) ApplyAnchor(drag, drag.Properties);
            }
        }
    }

    private (double Center, double[] Centers) MeasureRows(TabModel dragged)
    {
        var rows = TabStripProjection.Rows(_manager);
        var centers = new double[rows.Count];
        int draggedIndex = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            centers[i] = RowCenterY(rows[i]);
            if (ReferenceEquals(rows[i], dragged)) draggedIndex = i;
        }
        if (draggedIndex < 0 || double.IsNaN(centers[draggedIndex]))
            throw new InvalidOperationException(
                "drag row is not realized; crossings cannot be judged");
        return (centers[draggedIndex], centers);
    }

    /// <summary>
    /// Arranged center of a row in this control's coordinates, or NaN
    /// while the row has no layout to read (the same failure family the
    /// separator and selection-row reads guard).
    /// </summary>
    private double RowCenterY(TabModel tab)
    {
        if (RowElementOf(tab) is not { } item || item.ActualHeight <= 0)
            return double.NaN;
        try
        {
            return item.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y + item.ActualHeight / 2;
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// Drop: the manager already holds the final order (crossings
    /// committed mid-drag), so this is visual teardown only. The settle
    /// spring is the interaction's only inertia, carries the measured
    /// release velocity, and only runs under the motion gate; otherwise
    /// the row lands as a cut. The spring starts from the follow
    /// expression's last value, which is recomputed here -- stopping an
    /// animation reverts the property to its set value, and none was set.
    /// </summary>
    private void EndDrag(DragSession drag, bool settle, double velocity)
    {
        _drag = null;
        StopAutoscroll(drag);
        DetachGapMotion();
        var settled = false;
        try
        {
            var visual = drag.Visual;
            if (visual is not null)
            {
                visual.StopAnimation("Translation");
                visual.StopAnimation("Scale");
                visual.Scale = new Vector3(1, 1, 1);
                if (settle && drag.Properties is not null)
                {
                    // The spring needs a real start: the follow was an
                    // expression, so its value is recomputed and set.
                    visual.Properties.InsertVector3("Translation",
                        new Vector3(0, (float)(drag.LastPointerY - drag.AnchorY), 0));
                    var spring = visual.Compositor.CreateSpringVector3Animation();
                    spring.DampingRatio = TabStripMotion.SettleDampingRatio;
                    spring.Period = TimeSpan.FromMilliseconds(TabStripMotion.SettlePeriodMs);
                    spring.InitialVelocity = new Vector3(0, (float)velocity, 0);
                    spring.FinalValue = new Vector3(0, 0, 0);
                    var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                    // Subscribe before End (the gap-glide batches do the
                    // same), and guard by identity: the batch fires long
                    // after release, and a quick re-press on the same row
                    // has already armed a fresh follow on this item --
                    // a stale settle must not tear it down.
                    batch.Completed += (_, _) =>
                    {
                        if (_drag is { } live && ReferenceEquals(live.Item, drag.Item))
                        {
                            DragTrace("DRAG settle superseded");
                            return;
                        }
                        ResetDragVisual(drag);
                        DragTrace($"DRAG settle ghosts={CountLeakedMotion()}");
                    };
                    visual.StartAnimation("Translation", spring);
                    batch.End();
                    settled = true;
                }
            }
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            // The row's tree went away mid-teardown; nothing left to
            // hand back, and the reconcile owns the order regardless.
        }
        DragTrace($"DRAG end ghosts={CountLeakedMotion()}");
        if (!settled) ResetDragVisual(drag);
    }

    /// <summary>
    /// Hand the row's visual back: translation off lands it on its
    /// arranged slot as a cut, and the parked selection overlay catches
    /// up in the same pass.
    /// </summary>
    private void ResetDragVisual(DragSession drag)
    {
        try
        {
            if (drag.Visual is not null)
            {
                drag.Visual.StopAnimation("Translation");
                drag.Visual.StopAnimation("Scale");
                drag.Visual.Properties.InsertVector3("Translation", Vector3.Zero);
                drag.Visual.Scale = new Vector3(1, 1, 1);
            }
            if (drag.Item is not null)
                ElementCompositionPreview.SetIsTranslationEnabled(drag.Item, false);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex)) { }
        UpdateSelectionRow();
    }

    /// <summary>
    /// End the gesture restoring the pre-drag order. Committed crossings
    /// are already manager truth, so the rollback replays the projection
    /// diff through Move and PR 2's reconcile semantics do the repair.
    /// The visual goes home as a cut: the rollback churns the dragged
    /// row's container, so there is no visual left to spring, and state
    /// must not wait on motion anyway.
    /// </summary>
    private void CancelDrag(string reason)
    {
        if (_drag is not { } drag) return;
        if (drag.Machine.Phase != TabDragPhase.Dragging)
        {
            // The gesture never lifted: a press that stayed a click has
            // no trace pair and no visuals to tear down.
            _drag = null;
            return;
        }
        drag.Machine.Cancel();
        DragTrace($"DRAG cancel reason={reason}");
        EndDrag(drag, settle: false, velocity: 0);
        try
        {
            // Flags come home before the order does. A mid-drag boundary
            // crossing committed a SetPinned, and the pre-drag order is
            // not expressible until the pre-drag flags are back: Move
            // clamps against the boundary the flags define. Each restore
            // is its own relocation, so the order diff below has to be
            // computed against the state the flags leave behind.
            foreach (var tab in TabStripProjection.Rows(_manager))
            {
                bool wasPinned = drag.PreDragPinned.Contains(tab);
                if (tab.IsPinned == wasPinned) continue;
                _commitChurn = true;
                try { _manager.SetPinned(tab, wasPinned); }
                finally { _commitChurn = false; }
            }
            foreach (var op in TabStripProjection.Diff(
                drag.PreDragOrder, TabStripProjection.Rows(_manager)))
            {
                var from = _manager.IndexOf(op.Tab);
                if (from < 0 || from == op.To) continue;
                _commitChurn = true;
                try { _manager.Move(from, op.To); }
                finally { _commitChurn = false; }
            }
        }
        catch (InvalidOperationException)
        {
            // Tabs opened or closed mid-drag changed membership; the
            // pre-drag order is no longer expressible, so the committed
            // order stands rather than guess.
        }
    }

    /// <summary>
    /// Rows the strip still believes carry drag composition: gap glides
    /// not yet handed back, plus any teardown step that had to be
    /// abandoned. The oracle reads anything above zero as a leak.
    /// </summary>
    private int CountLeakedMotion() => _gapMotion.Count + _teardownFailures;

    /// <summary>
    /// Windows' animation-effects setting. Constructing UISettings can
    /// throw in packaged/sandboxed contexts (App.xaml.cs notes the same);
    /// unreadable is not "off", so the gate fails open and High Contrast
    /// still collapses the motion through its own pushed flag.
    /// </summary>
    private static bool SystemAnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return true;
        }
    }

    private static bool IsLayoutReadFailure(Exception ex)
        => ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException;
}
