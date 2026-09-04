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
using Anim = Microsoft.UI.Xaml.Media.Animation;

namespace Ghostty.Tabs;

/// <summary>
/// Fluent <see cref="NavigationView"/> vertical tab pane. Replaces the
/// ListView rail + chevron toggle.
/// </summary>
internal sealed partial class VerticalTabStrip : UserControl
{
    private const double RowInsetLeft = 4;
    private const double RowInsetVertical = 2;
    // Where a row's trailing glyph stops. The selected row's fill runs to
    // the pane edge, so this reads as padding inside the fill rather than
    // as a second inset.
    private const double RowInsetRight = 8;
    // What MUXC's NavigationViewItemPresenter reserves to the RIGHT of the
    // content it hosts, and no theme resource reaches all of: 4px from
    // LayoutRoot's NavigationViewItemButtonMargin, the ContentGrid's
    // hardcoded 14, and 8 from NavigationViewItemContentPresenterMargin
    // (generic.xaml, WindowsAppSDK 2.2). The chevron column adds nothing --
    // it is x:Load="False" and never realized. A row's content reclaims the
    // difference with a negative margin; vtab-strip-geometry.ps1 measures
    // the landed inset, so an SDK that moves this number fails there rather
    // than drifting silently.
    private const double NavItemTemplateRightGutter = 26;
    // Grouped members indent under their header: collapse hides members,
    // so this margin is the whole membership cue.
    private const double GroupInsetLeft = 14;

    private readonly TabManager _manager;
    private readonly Dictionary<TabModel, NavigationViewItem> _items = new();
    private readonly Dictionary<TabModel, TabHooks> _hooks = new();
    // Group headers. Top-level items like member rows (the flat projection
    // is what renders Edge-135), so MenuItems walkers must expect them.
    private readonly Dictionary<TabGroup, NavigationViewItem> _headers = new();
    // The header's one subscription. Membership is deliberately not
    // watched: it rides each member's own TabModel.Group notification, and
    // the projection that no longer names a dissolved group retires the row.
    private readonly Dictionary<TabGroup, AotBinding> _groupHooks = new();
    private bool _syncing;
    private bool _shellThemeActive;
    private ElementTheme _elementTheme = ElementTheme.Default;
    private SolidColorBrush? _defaultSelectedTabBgBrush;
    private SolidColorBrush? _selectedTabFillBrush;

    /// <summary>
    /// The field the active row is painted with. Rotated twin of the
    /// horizontal strip's: one brush for the row and for the seam cover over
    /// the pane border beside it.
    /// </summary>
    private readonly ActiveFieldFill _field = new();
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
    /// Per-row subscriptions, held together so a teardown cannot release
    /// one and leak the others. The group binding is body-only: a pinned
    /// tab cannot carry a group.
    /// </summary>
    private sealed record TabHooks(
        AotBinding Text,
        AotBinding Color,
        TabIconViewModel IconVm,
        PropertyChangedEventHandler IconHandler,
        AotBinding? Group)
    {
        public void Dispose()
        {
            Text.Dispose();
            Color.Dispose();
            IconVm.PropertyChanged -= IconHandler;
            Group?.Dispose();
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
        // Every row's anatomy follows the pane's width (full body-row
        // titles past the title threshold, icon-only below it), and this is
        // the one pass every width change rides -- cold start, toggle, and
        // the drag handle's live resize.
        if (_paneWidth != width)
        {
            _paneWidth = width;
            ApplyPaneWidthAnatomy();
        }
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

        // Under the selection fill and the separators, which both sit on
        // SelectionRowHost: the field is the ground, not another overlay.
        Canvas.SetZIndex(GroupFieldHost, -1);
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
        _manager.ActiveTabChanged += (_, _) =>
        {
            // Activation inside a collapsed group swaps which member the
            // projection keeps visible (Edge 135, no accordion), so the
            // reconcile lands before the selection sync that targets it.
            ReconcileRowOrder();
            SyncSelectionFromManager();
        };
        // Headers select for nothing; their one interaction is the toggle.
        NavView.ItemInvoked += OnNavItemInvoked;

        HookDragInput();
        Unloaded += (_, _) =>
        {
            CancelDrag("teardown");
            FinishPinFlight("teardown");
            StopAllFieldMotion();
            _pinnedPanel.StopMotion();
        };
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

        // Match horizontal TabHost: the active row is the field, painted the
        // terminal's own ground so it and the pane beside it are one surface
        // with no line between. The accent is the stroke around it
        // (UpdateSelectionRow's BorderBrush), never the fill.
        _selectedTabFillBrush = new SolidColorBrush(theme.ActiveTabFill);
        HideMuxcSelectedBackground();

        SetNavResource("NavigationViewSelectionIndicatorForeground", TransparentBrush);

        uint fieldPacked = PackColor(theme.ActiveTabFill);
        uint activePacked = PackColor(theme.ActiveTabInk);
        _shellActiveTextBrush = TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(fieldPacked, activePacked));

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
        // The field's wash and its terminals are scored against the
        // same ground this just moved. A push that recalibrates the ink
        // and not the field leaves the run washed for the frame the
        // strip no longer has -- and a fill-only push reaches no other
        // placement pass, so this is the one door it can arrive by.
        UpdateGroupFields();
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

    // The pinned shelf: the fixed band of icon squares, hosted as
    // NavView.PaneCustomContent. Pinned rows are NOT MenuItems -- they
    // must not scroll and must not take part in MUXC selection -- so they
    // get their own container and their own registry.
    private readonly StackPanel _pinnedShelf = new();
    private readonly TabPinBandPanel _pinnedPanel = new();

    /// <summary>The pane width the last ApplyPaneLayout named.</summary>
    private double _paneWidth;
    private readonly Dictionary<TabModel, VerticalTabPinnedRow> _pinnedRows = new();
    private readonly Dictionary<TabModel, TabHooks> _pinnedHooks = new();

    // The drop preview: an icon-only ghost slot promising where a body row
    // dragged over the shelf would land. It exists only while that promise
    // is deliverable -- unpinned row, pointer over the shelf -- and it is
    // strictly visual: it never touches manager state, and the drop that
    // honours it still commits through the zone grammar (Classify /
    // SetPinned / read-back), never through the ghost.
    private VerticalTabPinnedRow? _pinPreview;

    // The release-path flight at most one of: the ghost flying the pinned
    // row from its released slot to the prefix end. One field is the whole
    // identity guard -- a superseding flight, a teardown, or a completed
    // phase checks it before touching anything, so no stale callback can
    // reach into a flight that is no longer this one.
    private PinFlight? _pinFlight;

    /// <summary>One flight's live parts, released together on landing.</summary>
    private sealed class PinFlight
    {
        public required Ghostty.Shell.TabMorphGhost Ghost;
        public required Visual Visual;
        public required FrameworkElement Row;
        public required DispatcherQueueTimer Guard;
    }

    // The tab whose row held focus when a churn removed it, so the rebuilt
    // row can take the focus with it (AddItem). Focus is unique, so at
    // most one removal in a churn can set this; a candidate left over by a
    // tab that never comes back is inert -- the restore is by reference.
    private TabModel? _refocusTab;

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
    /// The band's breathing room below the last row of squares, before
    /// the scrolling list begins. Not a rule and not a gap wide enough to
    /// read as one: the zones are already told apart by shape, and this
    /// is only the seam the two structures need so neither touches.
    /// </summary>
    private const double BandInsetBottom = 4;

    /// <summary>
    /// One line in each gap between the scrolling list's rows, skipping both
    /// gaps that touch the selected row: those two edges are already drawn,
    /// in the accent, by the selected row's own top and bottom stroke.
    /// Drawing them again puts two lines a pixel apart.
    ///
    /// Only the list's gaps. The pinned squares live in the fixed band
    /// above the scroller, so the pin zone's edge is not a gap in this
    /// pool any more, and nothing draws it: the band is a different shape
    /// from the list, and that is the whole division.
    ///
    /// Rebuilt rather than kept in sync per item, because the thing being
    /// mirrored is MUXC's arranged layout, and the only honest read of that
    /// is to ask every item where it ended up.
    /// </summary>
    private void UpdateRowSeparators(bool selectionRowVisible)
    {
        // The shelf rides this refresh on purpose: it is the pass every
        // selection-placement and drag entry/exit path already calls, so
        // the band's own chrome never needs a caller of its own and
        // cannot be forgotten on an exit path.
        UpdatePinnedShelfChrome();

        // The group fields ride it for the same reason, and they have to
        // be placed BEFORE the separators below: the field's cap and end
        // bar are the run's boundaries, so the generic divider at those
        // two gaps is a second line saying the same thing in a weaker
        // voice, and the skip below needs the fields already standing.
        UpdateGroupFields();

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
            // A gap where a run begins or ends already has a line: the
            // field's cap or its end bar. Drawing the generic divider
            // there too doubles the rule and, worse, draws it in the
            // neutral separator shade right beside the group's own colour,
            // which reads as the field having a seam in it.
            if (!ReferenceEquals(tabs[i].Group, tabs[i + 1].Group)) continue;
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

    // ---------------------------------------------------------------
    // The group field.
    //
    // One Border per run, on its own canvas under everything else, sized
    // from the run's first and last realized rows. A container rather than
    // a decoration: the wash says "these belong together", the cap and the
    // end bar say where that stops. The grammar and every colour in it
    // live in TabGroupField, which the horizontal strip reads too -- the
    // two layouts are one language, and the arithmetic is pinned without a
    // window.
    //
    // Placed from measured rows for the same reason the selection fill and
    // the separators are: NavigationView owns the arrangement, and the only
    // honest read of it is to ask the items where they landed.
    // ---------------------------------------------------------------

    private readonly Dictionary<TabGroup, Border> _groupFields = new();
    /// <summary>
    /// One field clock per group, with the values it was flying to.
    ///
    /// The values are held because a Storyboard that is STOPPED puts the
    /// animated property back to its base, and for the fade that base is the
    /// zero the fade set on its way in. So a fade interrupted inside its 83ms
    /// -- a tab added above the run it had just created -- left an invisible
    /// Border that the following glide then animated the geometry of, and the
    /// field stayed gone until the group was dissolved and remade. Relying on
    /// Completed to put the value back is relying on Stop raising it, which is
    /// a framework detail this control should not be betting on either way.
    /// Landing is idempotent and both doors call it.
    /// </summary>
    private readonly Dictionary<TabGroup, FieldFlight> _fieldMotion = new();

    private readonly record struct FieldFlight(Anim.Storyboard Board, Action Land);

    private void UpdateGroupFields()
    {
        // Mid-drag the rows' arranged slots run ahead of their visuals
        // -- the glides ride the compositor -- so a field placed from
        // them would bracket a run whose members are still visibly
        // elsewhere. The last placement keeps matching what is on
        // screen, and the refresh at the drop catches up. Same rule the
        // selection row follows, for the same reason.
        if (_drag is not null) return;

        if (ActualWidth <= 0)
        {
            foreach (var (_, parked) in _groupFields) parked.Visibility = Visibility.Collapsed;
            return;
        }

        var rows = TabStripProjection.GroupedRows(_manager);
        var runs = TabGroupField.Runs(TabGroupField.SlotGroups(rows));
        // Read once per pass: UISettings is thread-affine and allocating
        // one per field would put the cost back that keeping this pass off
        // LayoutUpdated exists to avoid.
        var motion = TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast);
        var width = Math.Max(0, ActualWidth - RowInsetLeft);
        var placed = new HashSet<TabGroup>();
        // Once, not per run: the answer is the same for all of them and it
        // walks the template to find the scroller.
        var viewport = RowsViewport();

        foreach (var run in runs)
        {
            if (RowElementOfProjection(rows[run.First]) is not { } head) continue;
            if (RowElementOfProjection(rows[run.Last]) is not { } tail) continue;
            if (head.ActualHeight <= 0 || tail.ActualHeight <= 0) continue;

            double top, bottom;
            try
            {
                top = head.TransformToVisual(this).TransformPoint(new Point(0, 0)).Y;
                bottom = tail.TransformToVisual(this)
                    .TransformPoint(new Point(0, tail.ActualHeight)).Y;
            }
            catch (Exception ex) when (IsLayoutReadFailure(ex))
            {
                // A row that is not in the tree yet, or is leaving it. The
                // next refresh places the field.
                continue;
            }

            // Clipped to the band the rows are actually visible in, the way the
            // horizontal placement clips to its strip's viewport. A row that
            // has scrolled out of the list keeps reporting the offset it would
            // have had, so an unclipped field walks up behind the pinned shelf
            // and the strip's header -- and every pane surface up there is
            // forced transparent, so there is nothing to hide it. A run
            // entirely off-screen collapses to nothing and is skipped, which
            // hides its field rather than drawing it somewhere untrue.
            // The two ENDS are dropped when the clamp eats them, rather than
            // clamped along with the ground -- see PaintGroupField.
            var capVisible = true;
            var endVisible = true;
            if (viewport is { } band)
            {
                capVisible = top >= band.Top;
                endVisible = bottom <= band.Bottom;
                top = Math.Max(top, band.Top);
                bottom = Math.Min(bottom, band.Bottom);
            }

            if (bottom - top <= 0) continue;
            placed.Add(run.Group);
            PlaceGroupField(run.Group, top, width, bottom - top, motion, capVisible, endVisible);
        }

        // A field the manager still holds is only HIDDEN when it could not be
        // placed this pass -- its rows are mid-rebuild, or it scrolled out --
        // because the Border is what carries the glide, and destroying it makes
        // the next placement an appearance instead of a move.
        //
        // That distinction is the whole of it. Retiring the field alongside the
        // header row read as tidy and was not: a collapse changes how many rows
        // the strip shows, ReconcileRowOrder answers a changed count with
        // RebuildAllItems, and that removes and re-adds every group's row. So
        // every field on the strip was destroyed and re-faded on any collapse,
        // any expand, and any group being created or dissolved -- the four
        // events the glide exists for, and the only ones where a field changes
        // size. It cut and faded instead, and the guard that pinned the field
        // to the header row's lifetime pinned that in place.
        var retired = new List<TabGroup>();
        foreach (var (group, field) in _groupFields)
        {
            if (placed.Contains(group)) continue;
            StopFieldMotion(group);
            if (_manager.Groups.Contains(group))
            {
                field.Visibility = Visibility.Collapsed;
                continue;
            }
            retired.Add(group);
        }
        foreach (var group in retired) RemoveGroupField(group);
    }

    /// <summary>
    /// The element a projected row renders as, or null while the strip has
    /// not built it. Headers and members both, because the field's cap IS
    /// the header row: a field measured from the first member instead would
    /// leave its own header sitting outside it.
    /// </summary>
    private FrameworkElement? RowElementOfProjection(TabStripProjection.ProjectedRow row)
        => row switch
        {
            TabStripProjection.ProjectedRow.Header { Group: { } group }
                => _headers.TryGetValue(group, out var header) ? header : null,
            TabStripProjection.ProjectedRow.Item { Tab: { } tab }
                => _items.TryGetValue(tab, out var item) ? item : null,
            _ => null,
        };

    private void PlaceGroupField(
        TabGroup group, double top, double width, double height, bool motion,
        bool capVisible, bool endVisible)
    {
        var appearing = false;
        if (!_groupFields.TryGetValue(group, out var field))
        {
            field = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(TabGroupField.CornerRadiusPx),
            };
            _groupFields[group] = field;
            GroupFieldHost.Children.Add(field);
            appearing = true;
        }

        if (field.Visibility != Visibility.Visible)
        {
            appearing = true;
            field.Visibility = Visibility.Visible;
        }

        PaintGroupField(field, group, capVisible, endVisible);
        field.Width = width;
        Canvas.SetLeft(field, RowInsetLeft);

        var fromTop = Canvas.GetTop(field);
        var fromHeight = field.Height;
        StopFieldMotion(group);

        // A field that has never been placed has nothing to travel from,
        // and one arriving fades in instead of sliding out of a corner.
        if (appearing || !motion || double.IsNaN(fromTop) || double.IsNaN(fromHeight))
        {
            Canvas.SetTop(field, top);
            field.Height = height;
            field.Opacity = 1;
            if (appearing && motion) FadeInGroupField(group, field);
            return;
        }

        // Nothing to travel: land on the NEW target rather than returning.
        // StopFieldMotion has already written the abandoned flight's target
        // over the property, so returning here leaves the field wherever that
        // was -- a difference under half a pixel from where it should be, but
        // only if the old target and the new one agree, which is exactly what
        // this branch does not check.
        if (Math.Abs(fromTop - top) < 0.5 && Math.Abs(fromHeight - height) < 0.5)
        {
            Canvas.SetTop(field, top);
            field.Height = height;
            return;
        }

        // The travel starts from where the field WAS, read before the stop.
        // Without it the glide interpolates from whatever the property held at
        // Begin -- and by then StopFieldMotion has landed the abandoned
        // flight's target on it, so an interrupted glide jumped forward to the
        // old destination and eased from there.
        GlideGroupField(group, field, fromTop, fromHeight, top, height);
    }

    /// <summary>
    /// The three parts, all three off the strip's own ground: the wash as
    /// translucent ink so Mica still shows through it, and the cap and end
    /// bar as the group's colour lifted to the non-text floor against the
    /// field it sits on.
    /// </summary>
    private void PaintGroupField(
        Border field, TabGroup group, bool capVisible, bool endVisible)
    {
        field.Background = TabColorBrush.FromPackedArgb(
            TabGroupField.WashArgb(_stripBackdropPacked));
        field.BorderBrush = TabColorBrush.FromPackedRgb(
            TabGroupField.TerminalRgb(_stripBackdropPacked, group.Color));
        // Vertical: the cap runs along the header row's leading edge and
        // the end bar closes the run under its last member. The sides stay
        // open -- the field meets the pane on its right the way the
        // selected row does, and a fourth edge would box it in.
        //
        // An end the viewport clipped away is DROPPED, not moved. The ground
        // can be clamped to the visible band harmlessly, but these two edges
        // are statements: the cap says "the run starts here" and the end bar
        // says "it ends here". Clamped along with the ground they would say it
        // about whichever row happens to be at the edge of the scroller, which
        // is a lie the horizontal strip does not tell -- PlaceFieldTerminal
        // refuses a bar outside the viewport rather than sliding it inward.
        field.BorderThickness = new Thickness(
            0, capVisible ? TabGroupField.TerminalThicknessPx : 0,
            0, endVisible ? TabGroupField.TerminalThicknessPx : 0);
    }

    /// <summary>
    /// The field follows its rows on their own clock: the strip's Existing
    /// Elements glide, one Storyboard so the top edge and the bottom edge
    /// can never arrive on different frames and shear the container.
    ///
    /// Dependent animations, both of them -- Canvas.Top and Height are
    /// neither composition-backed -- which is affordable here and nowhere
    /// else in this control: there is one field per group, not one per row.
    /// </summary>
    private void GlideGroupField(
        TabGroup group, Border field,
        double fromTop, double fromHeight, double top, double height)
    {
        var board = new Anim.Storyboard();
        board.Children.Add(FieldGlide(field, "(Canvas.Top)", fromTop, top));
        board.Children.Add(FieldGlide(field, "Height", fromHeight, height));
        // The values land whether or not the clock is ever serviced: a
        // Storyboard that is stopped (teardown, a second change mid-glide)
        // leaves the property where it was, and the field would keep the
        // old geometry while the rows moved on.
        void Land()
        {
            Canvas.SetTop(field, top);
            field.Height = height;
        }
        // Only the flight that is still the CURRENT one may retire the entry.
        // WinUI 3 raises Completed from Stop (unlike WPF), so an abandoned
        // board's handler runs after its replacement has already been stored:
        // it deleted the replacement's entry and wrote its own target over a
        // property the replacement was animating. That left the new board
        // unreachable -- the next StopFieldMotion found nothing, so a third
        // glide began while the second was still running two clocks on
        // Canvas.Top, and StopAllFieldMotion could no longer stop it at
        // Unloaded, which is the leak that method exists to prevent.
        board.Completed += (_, _) =>
        {
            if (!_fieldMotion.TryGetValue(group, out var current)
                || !ReferenceEquals(current.Board, board)) return;
            _fieldMotion.Remove(group);
            Land();
        };        _fieldMotion[group] = new FieldFlight(board, Land);
        board.Begin();
    }

    /// <summary>
    /// One glided property, on the gap glide's own curve.
    ///
    /// A spline keyframe rather than a DoubleAnimation with an easing
    /// function: the strip states this curve by its two control points
    /// everywhere else (the composition gap glide, the pin flight), and
    /// the Storyboard clock's stock easings are a different family of
    /// curves entirely. KeySpline is the same parameterization, so the
    /// field and the rows it is drawn around decelerate together instead
    /// of merely finishing together.
    /// </summary>
    private static Anim.DoubleAnimationUsingKeyFrames FieldGlide(
        Border field, string property, double from, double to)
    {
        var glide = new Anim.DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        // The start is STATED, at time zero, not inferred from the property.
        // Inferred, the curve begins wherever the value sits when Begin runs --
        // and by then the abandoned flight's landing has written its own target
        // there, so an interrupted glide jumped forward to the old destination
        // and eased out of it.
        glide.KeyFrames.Add(new Anim.DiscreteDoubleKeyFrame
        {
            KeyTime = Anim.KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from,
        });
        glide.KeyFrames.Add(new Anim.SplineDoubleKeyFrame
        {
            KeyTime = Anim.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TabGroupField.GlideMs)),
            Value = to,
            KeySpline = new Anim.KeySpline
            {
                ControlPoint1 = new Point(TabGroupField.GlideEaseX1, TabGroupField.GlideEaseY1),
                ControlPoint2 = new Point(TabGroupField.GlideEaseX2, TabGroupField.GlideEaseY2),
            },
        });
        Anim.Storyboard.SetTarget(glide, field);
        Anim.Storyboard.SetTargetProperty(glide, property);
        return glide;
    }

    /// <summary>
    /// A field that has just arrived fades in on the Fade token rather
    /// than popping: a group is created by a command the user just issued,
    /// and the container appearing under their cursor at full strength
    /// reads as a flash.
    /// </summary>
    private void FadeInGroupField(TabGroup group, Border field)
    {
        field.Opacity = 0;
        var fade = new Anim.DoubleAnimation
        {
            // From stated rather than inferred. Without it the animation's base
            // is whatever the property held when it began -- the zero on the
            // line above -- and that is the value a Stop restores.
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(TabGroupField.FadeMs)),
        };
        Anim.Storyboard.SetTarget(fade, field);
        Anim.Storyboard.SetTargetProperty(fade, "Opacity");
        var board = new Anim.Storyboard();
        board.Children.Add(fade);
        void Land() => field.Opacity = 1;
        // Only the flight that is still the CURRENT one may retire the entry.
        // WinUI 3 raises Completed from Stop (unlike WPF), so an abandoned
        // board's handler runs after its replacement has already been stored:
        // it deleted the replacement's entry and wrote its own target over a
        // property the replacement was animating. That left the new board
        // unreachable -- the next StopFieldMotion found nothing, so a third
        // glide began while the second was still running two clocks on
        // Canvas.Top, and StopAllFieldMotion could no longer stop it at
        // Unloaded, which is the leak that method exists to prevent.
        board.Completed += (_, _) =>
        {
            if (!_fieldMotion.TryGetValue(group, out var current)
                || !ReferenceEquals(current.Board, board)) return;
            _fieldMotion.Remove(group);
            Land();
        };        _fieldMotion[group] = new FieldFlight(board, Land);
        board.Begin();
    }

    /// <summary>
    /// Every field clock, stopped. Teardown only: a Storyboard left running
    /// on an unloaded element is the same leak the drag's glide census
    /// exists to catch, one door along.
    /// </summary>
    private void StopAllFieldMotion()
    {
        // Teardown does not land: the element is going away, and writing a
        // final geometry onto an unloaded Border is work for nobody.
        foreach (var (_, flight) in _fieldMotion) flight.Board.Stop();
        _fieldMotion.Clear();
    }

    private void StopFieldMotion(TabGroup group)
    {
        if (!_fieldMotion.Remove(group, out var flight)) return;
        flight.Board.Stop();
        // Landed here rather than left to Completed, which a Stop may never
        // raise. An interrupted fade's base value is zero, so the alternative
        // is a field that is still placed, still measured, and invisible.
        flight.Land();
    }

    /// <summary>
    /// Retire a dissolved group's field.
    ///
    /// Driven by whether the MANAGER still holds the group, not by the header
    /// row's lifetime: the row is removed and re-added by any rebuild, and a
    /// field that came and went with it could never glide across the changes
    /// that resize it. A field outliving its run is prevented by the same
    /// check, one pass later.
    /// </summary>
    private void RemoveGroupField(TabGroup group)
    {
        StopFieldMotion(group);
        if (!_groupFields.Remove(group, out var field)) return;
        GroupFieldHost.Children.Remove(field);
    }

    /// <summary>
    /// The filled row behind the selected tab. Exposed so MainWindow can
    /// measure where it ends and cover the pane border for exactly that
    /// span, the way the horizontal strip's seam is covered.
    /// </summary>
    internal FrameworkElement SelectionRowElement => SelectionRow;

    /// <summary>
    /// Whether the active row REACHES the pane border, which is the premise
    /// the seam cover rests on: the cover is placed from the row's right
    /// edge, and that edge is the border it is hiding.
    ///
    /// False for a pinned square. A square is 40px wide wherever it sits in
    /// the band, so its right edge is somewhere in the middle of the strip
    /// -- and the cover, placed there, is a bar of terminal colour drawn
    /// across the band's own gutter with the pane border it exists to hide
    /// still standing untouched beside the square.
    ///
    /// This is the same fact <c>UpdateSelectionRow</c> already spends on the
    /// selection fill's fourth stroke ("a square meets nothing"), asked
    /// where the cover can hear it.
    /// </summary>
    internal bool ActiveRowMeetsThePane
        => RowElementOf(_manager.ActiveTab) is not VerticalTabPinnedRow;

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
        // The fill takes the shape of what it marks. A body row spans the
        // lane and meets the terminal at the pane's edge; a pinned square
        // is a square sitting inside a band, and a lane-wide bar behind it
        // would mark four slots the tab does not occupy. The square also
        // carries no margin of its own -- the band owns every gutter -- so
        // its box is its box, with no inset to subtract.
        var square = item is VerticalTabPinnedRow;
        var rowHeight = square
            ? item.ActualHeight
            : Math.Max(0, item.ActualHeight - RowInsetVertical * 2);
        var rowWidth = square
            ? item.ActualWidth
            : Math.Max(0, ActualWidth - RowInsetLeft);

        SelectionRow.Width = rowWidth;
        SelectionRow.Height = rowHeight;
        Canvas.SetLeft(SelectionRow, square ? topLeft.X : RowInsetLeft);
        Canvas.SetTop(SelectionRow, square ? topLeft.Y : topLeft.Y + RowInsetVertical);
        SelectionRow.CornerRadius = new CornerRadius(0);
        // The row wears the field's own brush, settled onto the colour this
        // tab rests at. MainWindow's vertical seam cover reads its fill
        // straight back off this Background, so handing over the instance is
        // what keeps the cover and the row on one clock through the settle.
        _field.Settle(
            _manager.ActiveTab,
            ResolveSelectionRowFill(_manager.ActiveTab).Color,
            StripGround(),
            TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast));
        SelectionRow.Background = _field.Brush;

        // The same folder stroke the horizontal strip gets, rotated: a body
        // row meets the pane along its right edge, so that is the side left
        // open and the other three carry the pane's own border colour. A
        // square meets nothing -- it sits inside the band with list rows
        // below and band gutters around it -- so it is closed on all four.
        // A tab with a preset colour is stroked in that colour, matching
        // its pane.
        SelectionRow.BorderBrush = _manager.ActiveTab.Color != TabColor.None
            ? TabColorBrush.From(TabColorPalette.Border(_manager.ActiveTab.Color))
            : AccentBrush;
        SelectionRow.BorderThickness = square
            ? new Thickness(1)
            : new Thickness(1, 1, 0, 1);

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

    /// <summary>The strip's own ground, as a colour: what the field grows out of.</summary>
    private Color StripGround() => Color.FromArgb(
        0xFF,
        (byte)(_stripBackdropPacked >> 16),
        (byte)(_stripBackdropPacked >> 8),
        (byte)_stripBackdropPacked);

    private SolidColorBrush ResolveSelectionRowFill(TabModel tab)
    {
        if (tab.Color != TabColor.None)
            return TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: true));

        // Mirror horizontal TabHost: both paths now fill the selected handle
        // with the TERMINAL background, so the row meets the pane with no line
        // between whatever window-theme says. The accent's job here is the
        // stroke on the row's three closed sides, not the fill.
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

        // Headers are never the active row, so their ink is always the
        // muted text brush; the swatch keeps its own palette fill (the
        // group color is content, not chrome).
        foreach (var (_, item) in _headers)
            if (item.Content is VerticalTabGroupHeaderRow header)
                header.ApplyInk(ResolveInactiveTextBrush());
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
    ///
    /// The loaded-but-not-realized state is the same fence one level down,
    /// and it is the churn crash (0xC000027B / XAML 800F1000) read out of
    /// a full dump: a rebuild writes MenuItems, MUXC realizes containers
    /// across the NEXT layout pass, and set_SelectedItem inside that
    /// window resolves the item's container as the host's base-class
    /// ContentControl -- the selection style targets NavigationViewItem,
    /// the application fails, XAML stows it, and the dispatcher turn ends
    /// in a fail-fast. The assignment therefore waits until the selected
    /// item is realized (IsLoaded), on a latched LayoutUpdated replay;
    /// deferring again re-arms the latch, so a re-churn cannot strand it.
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

        var item = _items.TryGetValue(_manager.ActiveTab, out var selected)
            ? selected
            : null;
        if (item is not null && !item.IsLoaded)
        {
            DeferSelectionSync(item);
            return;
        }
        _selectionSyncDeferred = false;

        _syncing = true;
        try
        {
            // While the active tab is pinned it has no MenuItems entry at
            // all, and MUXC has nothing that can be selected. Leaving the
            // previous selection standing would keep a body row painted as
            // selected while the strip's active chrome sits on a pinned
            // row, so park the selection at null: the selection overlay is
            // the active chrome for a pinned row and does not consult MUXC.
            NavView.SelectedItem = item;
        }
        finally { _syncing = false; }

        ApplyAllItemTabColors();
        RecolorNavItems();
        EnsureActiveItemVisible();
        ScheduleSelectionLayoutPass(retryIfZeroBounds: true);
    }

    /// <summary>
    /// The realization latch for the selection sync, held on the deferred
    /// item itself: its Loaded is the realization event -- MUXC raises it
    /// when the container lands in the tree -- and the handler detaches
    /// before re-running the sync, so a re-churn that produced a fresh
    /// unrealized element simply re-defers onto the new one. No standing
    /// strip-rooted subscription: the handler dies with the element it
    /// rides, and teardown owes it nothing.
    /// </summary>
    private NavigationViewItem? _selectionRealizationItem;

    private void DeferSelectionSync(NavigationViewItem item)
    {
        _selectionSyncDeferred = true;
        if (ReferenceEquals(_selectionRealizationItem, item)) return;
        _selectionRealizationItem = item;
        item.Loaded -= OnSelectionRealized;
        item.Loaded += OnSelectionRealized;
    }

    private void OnSelectionRealized(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationViewItem item)
        {
            item.Loaded -= OnSelectionRealized;
            if (ReferenceEquals(_selectionRealizationItem, item))
                _selectionRealizationItem = null;
        }
        _selectionSyncDeferred = false;
        SyncSelectionFromManager();
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

 
    /// <summary>
    /// The band the scrolling rows are visible in, in this control's own
    /// coordinates.
    ///
    /// Not <see cref="SelectionViewport"/>: that one answers for whichever
    /// container the ACTIVE row lives in, and returns the pinned shelf when the
    /// active tab is pinned. A group's rows are never pinned, so a field
    /// clamped to the shelf would collapse to nothing every time the user
    /// happened to be on a pinned tab.
    /// </summary>
    private (double Top, double Bottom)? RowsViewport()
    {
        _menuItemsScroller ??= FindDescendantByName(NavView, "MenuItemsScrollViewer");
        if (_menuItemsScroller is not { ActualHeight: > 0 } scroller) return null;
        try
        {
            var top = scroller.TransformToVisual(this)
                .TransformPoint(new Point(0, 0)).Y;
            return (top, top + scroller.ActualHeight);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
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
    /// The pinned section: one band of icon squares, and nothing else.
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
        // No label and no rule: the zone is announced by structure. A
        // pinned tab is an icon square and a body row is a row, and two
        // shapes stacked need nothing drawn between them. (Per-square
        // "Pinned" ItemStatus keeps the zone in the automation tree.)
        //
        // The band takes the rows' own inset on BOTH sides -- its first
        // column starts on the vertical the list's icons do, and its last
        // one stops the same distance short of the pane edge, so the
        // column count is what the pane can hold rather than what it can
        // hold flush. A breath at top and bottom so neither structure
        // touches the other.
        _pinnedPanel.Margin = new Thickness(
            RowInsetLeft, RowInsetVertical, RowInsetLeft, BandInsetBottom);
        _pinnedPanel.HorizontalAlignment = HorizontalAlignment.Left;

        _pinnedShelf.Children.Add(_pinnedPanel);
        _pinnedShelf.Visibility = Visibility.Collapsed;

        NavView.PaneCustomContent = _pinnedShelf;
    }

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
        TabDragTrace.Line($"DIAG rebuild enter items={NavView.MenuItems.Count} t={Environment.TickCount64}");
        // Remove by what we hold, not by what the manager still has:
        // on a Reset the manager is already empty and rows we own would
        // otherwise stay in their container with their subscriptions live.
        foreach (var group in _groupHooks.Keys.ToArray())
            RemoveGroupRow(group);
        foreach (var tab in _hooks.Keys.Concat(_pinnedHooks.Keys).ToArray())
            RemoveItem(tab);
        // Row order comes from the projector, the same source the
        // horizontal strip reconciles against, so the two strips cannot
        // disagree by construction. Headers and member rows land as
        // top-level items in projection order -- flat, because the
        // Edge-135 shape cannot render nested (MUXC hides every child of
        // a collapsed item, and the rule keeps the active member visible).
        foreach (var projected in TabStripProjection.GroupedRows(_manager))
        {
            switch (projected)
            {
                case TabStripProjection.ProjectedRow.Header { Group: { } group }:
                    AddGroupRow(group);
                    break;
                case TabStripProjection.ProjectedRow.Item { Tab: { } tab }:
                    AddItem(tab);
                    break;
            }
        }
        UpdatePinnedShelfChrome();
        TabDragTrace.Line($"DIAG rebuild walked items={NavView.MenuItems.Count} t={Environment.TickCount64}");
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
                TabDragTrace.Line($"DIAG churn {e.Action} t={Environment.TickCount64}");
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

        // A churn rebuild (Move's Remove+Add, a pin relocation, a
        // RebuildAllItems) removed the element that held focus and
        // inserted a fresh one. Without this hand-off a keyboard unpin of
        // the focused row drops focus out of the strip entirely -- the
        // panel is not a control, nothing re-takes it, and the arrows go
        // dead until a click. The candidate is only set by RemoveItem
        // when the removed element actually held focus, so a plain
        // rebuild that churns an unfocused row restores nothing.
        if (!ReferenceEquals(_refocusTab, tab)) return;
        _refocusTab = null;
        RowElementOf(tab)?.Focus(FocusState.Programmatic);
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

        // Title, bell, and idle are cheap to reapply, so they share one
        // binding. Color is separate because it triggers a whole-strip
        // recolor, and the icon is separate because its spec lives on
        // TabIconViewModel and changes when the foreground process
        // changes. Folding them all together would re-decode the icon
        // bitmap and recolor every row on every OSC 0/2 title the shell
        // emits.
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
        nameof(TabModel.BellRinging),
        nameof(TabModel.IsIdle));

        var colorBinding = AotBinding.Create(tab, _ => RefreshTabColors(),
            nameof(TabModel.Color));

        // Membership has no manager event: TabModel.Group raising is the
        // only carrier. Deferred so a multi-tab op coalesces.
        var groupBinding = AotBinding.Create(tab, _ => OnTabGroupStateChanged(tab),
            nameof(TabModel.Group));

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
        _hooks[tab] = new TabHooks(textBinding, colorBinding, vm, iconHandler, groupBinding);
        ApplyRowInsets(item, tab);

        // One seam into the shelf: Up from the first body row walks into
        // it. Every other arrow reaches MUXC's own traversal untouched.
        item.KeyDown += OnBodyRowKeyDown;

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
        // pinned row instead: title, bell, and idle feed the tooltip and
        // the badges, color re-inks the whole strip, and the icon rebuilds
        // when the foreground process changes. (AddBodyRow carries the
        // long version of the split rationale.)
        var textBinding = AotBinding.Create(tab, _ =>
        {
            if (_pinnedRows.TryGetValue(tab, out var pinnedRow))
                pinnedRow.Refresh(tab);
        },
        nameof(TabModel.EffectiveTitle),
        nameof(TabModel.ShellReportedTitle),
        nameof(TabModel.UserOverrideTitle),
        nameof(TabModel.BellRinging),
        nameof(TabModel.IsIdle));

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
        _pinnedHooks[tab] = new TabHooks(textBinding, colorBinding, vm, iconHandler, Group: null);
        _pinnedPanel.Children.Add(row);

        // Outside MUXC, the row owns its own keyboard story: Enter/Space
        // activate through the same fenced path a shelf click uses, and
        // Up/Down walk within the shelf and across the boundary. Focus
        // and pointer hover share one painter, so neither state can erase
        // the other and the row has no focus rect of its own to lean on.
        row.KeyDown += OnPinnedRowKeyDown;
        row.GotFocus += OnPinnedRowFocusVisual;
        row.LostFocus += OnPinnedRowFocusVisual;
        row.PointerEntered += OnShelfRowPointerEntered;
        row.PointerExited += OnShelfRowPointerExited;
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
        AutomationProperties.SetItemStatus(item, tab.Group is { } group
            ? TabAccessibleText.Status(tab.IsPinned, tab.BellRinging, group.Title, group.IsCollapsed)
            : TabAccessibleText.Status(tab));
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
            // Remember whether the row held focus before it goes: its
            // replacement (same tab, rebuilt in the other container on a
            // zone crossing) should take the focus with it (AddItem).
            if (pinned.FocusState != FocusState.Unfocused) _refocusTab = tab;
            _pinnedPanel.Children.Remove(pinned);
            if (_pinnedHooks.Remove(tab, out var pinnedHooks))
                pinnedHooks.Dispose();
            return;
        }

        if (!_items.TryGetValue(tab, out var item)) return;
        if (item.FocusState != FocusState.Unfocused) _refocusTab = tab;

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
    /// Build the header row <paramref name="group"/> renders as. It never
    /// selects, so selection chrome stays on the member rows around it.
    /// </summary>
    private void AddGroupRow(TabGroup group)
    {
        if (_headers.ContainsKey(group)) return;
        var item = new VerticalTabGroupHeaderItem
        {
            Tag = group,
            SelectsOnInvoked = false,
            Content = new VerticalTabGroupHeaderRow(group, _manager.MembersOf(group).Count),
        };
        // The header's UIA ExpandCollapse pattern is keyboard-equivalent:
        // it lands on the strip's command event (routes through the
        // router, announces), never on the chevron's direct toggle.
        item.GroupToggleRequested += (_, e) =>
            GroupToggleFromCommandRequested?.Invoke(this, e);
        ApplyGroupChrome(item, group);
        ApplyHeaderAnatomy(item);

        var binding = AotBinding.Create(group, _ => ScheduleReconcile(),
            nameof(TabGroup.IsCollapsed), nameof(TabGroup.Title), nameof(TabGroup.Color));
        _headers[group] = item;
        _groupHooks[group] = binding;

        // Keyboard invoke: handled HERE, before MUXC's list sees the key,
        // or the same Enter also arrives as ItemInvoked and toggles twice.
        item.KeyDown += OnGroupHeaderKeyDown;

        _syncing = true;
        try { NavView.MenuItems.Add(item); }
        finally { _syncing = false; }
    }

    private void RemoveGroupRow(TabGroup group)
    {
        // The field is NOT retired here. This runs on every rebuild, including
        // the one a collapse triggers, and the field has to survive that to
        // glide across it. UpdateGroupFields retires it when the manager stops
        // holding the group.
        if (!_headers.Remove(group, out var item)) return;
        // One fence rule for every MenuItems mutation.
        _syncing = true;
        try { NavView.MenuItems.Remove(item); }
        finally { _syncing = false; }
        if (_groupHooks.Remove(group, out var hooks))
            hooks.Dispose();
    }

    /// <summary>
    /// Everything on the header item that follows the group: tooltip, name,
    /// and the collapse state on ItemStatus. (The ExpandCollapse pattern is
    /// 5b-2's, with the group commands.)
    /// </summary>
    private static void ApplyGroupChrome(NavigationViewItem item, TabGroup group)
    {
        ToolTipService.SetToolTip(item, group.Title);
        AutomationProperties.SetName(item, group.Title);
        AutomationProperties.SetItemStatus(
            item, group.IsCollapsed ? "Collapsed" : string.Empty);
    }

    /// <summary>
    /// A body row's two insets. Left: grouped members indent, ungrouped
    /// rows do not -- collapse re-parents nothing, so a member leaving a
    /// group un-indents through this same pass. Right: the template gutter
    /// the row reclaims, so its close glyph lands where the pinned rows'
    /// and the headers' trailing edges do.
    /// </summary>
    private void ApplyRowInsets(NavigationViewItem item, TabModel tab)
    {
        if (item.Content is not VerticalTabNavRow row) return;
        row.ShowClose = ShowsTitles;
        row.Margin = new Thickness(
            tab.Group is null ? 0 : GroupInsetLeft, 0, ContentInsetRight, 0);
    }

    /// <summary>
    /// A group header's anatomy: past the title threshold it is the swatch,
    /// the group's name, its member count and the chevron; below it the
    /// name and the count go, because the 48px rail's content slot is
    /// narrower than they are and a header that keeps them spills its
    /// chevron past the pane edge. What survives is what the rail can say:
    /// the group's colour, and whether it is folded.
    /// </summary>
    private void ApplyHeaderAnatomy(NavigationViewItem item)
    {
        if (item.Content is not VerticalTabGroupHeaderRow row) return;
        row.ShowTitle = ShowsTitles;
        row.Margin = new Thickness(0, 0, ContentInsetRight, 0);
    }

    /// <summary>
    /// Pane width at or above which the strip's rows show titles. The
    /// compact pane is 48px wide; the expanded pane 220. Anything at or
    /// past this is wide enough to read a trimmed title.
    /// </summary>
    private const double TitlePaneWidthThreshold = 96;

    /// <summary>
    /// Whether the pane is wide enough to read a title. One threshold for
    /// everything that carries one: the body rows and the group headers
    /// change anatomy at the same width, or the rail degrades in pieces.
    /// The pinned band answers to no width -- an icon square is an icon
    /// square in a 48px rail and in a 220px sidebar alike.
    /// </summary>
    private bool ShowsTitles
        => _paneWidth >= TitlePaneWidthThreshold;

    /// <summary>
    /// The right margin a row's content wears to reclaim MUXC's reserved
    /// gutter, so its trailing glyph stops <see cref="RowInsetRight"/> from
    /// the pane edge. Only past the title threshold: the compact rail's
    /// content is already arranged outside the pane by that same template,
    /// and widening it there would push it further out.
    /// </summary>
    private double ContentInsetRight
        => ShowsTitles ? RowInsetRight - NavItemTemplateRightGutter : 0;

    private void OnTabGroupStateChanged(TabModel tab)
    {
        // Chrome is immediate; the layout answer is the deferred reconcile,
        // so a multi-tab op coalesces.
        if (_items.TryGetValue(tab, out var item))
        {
            ApplyItemTitleChrome(item, tab);
            ApplyRowInsets(item, tab);
        }
        ScheduleReconcile();
    }

    private bool _reconcileScheduled;

    /// <summary>
    /// Group changes arrive one property at a time (no manager event behind
    /// them); one dispatcher pass keeps a burst from running a reconcile
    /// per mutation.
    /// </summary>
    private void ScheduleReconcile()
    {
        if (_reconcileScheduled) return;
        _reconcileScheduled = true;
        var queuedAt = Environment.TickCount64;
        TabDragTrace.Line($"DIAG reconcile queued t={queuedAt}");
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _reconcileScheduled = false;
            TabDragTrace.Line($"DIAG reconcile run t={Environment.TickCount64} delta={Environment.TickCount64 - queuedAt}");
            ReconcileRowOrder();
            SyncSelectionFromManager();
            TabDragTrace.Line($"DIAG reconcile done t={Environment.TickCount64}");
        });
    }

    /// <summary>
    /// The header's only interaction. A live drag stands it down, as every
    /// other row mutation: collapsing reorders visible rows under a live
    /// gesture.
    /// </summary>
    /// <remarks>
    /// The gate is phase-aware on purpose. A header press now arms a drag
    /// session immediately, and on a plain click MUXC raises ItemInvoked
    /// from its own release handler -- deeper in the tree, so BEFORE the
    /// strip's release handler clears the still-unlifted session. A
    /// session-exists gate would eat every header click; only a gesture
    /// that actually lifted (Dragging) is a drag, and a lifted gesture
    /// holds the pointer capture, so MUXC never raises ItemInvoked for it
    /// at all.
    /// </remarks>
    private void ToggleGroup(TabGroup group)
    {
        if (_drag is { Machine.Phase: TabDragPhase.Dragging }) return;
        _syncing = true;
        try { _manager.CollapseGroup(group, !group.IsCollapsed); }
        finally { _syncing = false; }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Body rows activate through SelectionChanged; a header cannot
        // raise that (SelectsOnInvoked=false), so ItemInvoked is the
        // pointer path for the toggle and nothing else.
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: TabGroup group })
            return;
        // Pointer-only: the keyboard toggle already sits on the header,
        // so only this path can hide the row that holds focus.
        if (!group.IsCollapsed) RestoreFocusUnder(group);
        ToggleGroup(group);
    }

    /// <summary>
    /// Collapse hides member rows in place -- no churn, so the _refocusTab
    /// hand-off in AddItem never fires and a focused member's focus would
    /// drop unmanaged. Land it on the group's header, and only when the
    /// folding group is the one holding focus: an unrelated collapse must
    /// not move focus at all.
    /// </summary>
    private void RestoreFocusUnder(TabGroup group)
    {
        if (FocusManager.GetFocusedElement()
            is not NavigationViewItem { Tag: TabModel focused }) return;
        if (!ReferenceEquals(focused.Group, group)) return;
        if (_headers.TryGetValue(group, out var header))
            header.Focus(FocusState.Programmatic);
    }

    private void OnGroupHeaderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space))
            return;
        e.Handled = true;
        // The keyboard gesture is a command, not the watched chevron: it
        // routes through the host to the router, which announces it, and
        // comes back through ToggleGroupFromCommand for the re-home and
        // the toggle.
        if (sender is NavigationViewItem { Tag: TabGroup group })
            GroupToggleFromCommandRequested?.Invoke(this, (group, !group.IsCollapsed));
    }

    /// <summary>
    /// Raised when a keyboard gesture (or, in 2b, the header's UIA
    /// ExpandCollapse pattern) asks a group to toggle. The pointer chevron
    /// stays direct: the user is watching it land, so it announces nothing
    /// and needs no router round trip.
    /// </summary>
    internal event EventHandler<(TabGroup Group, bool Collapsed)>? GroupToggleFromCommandRequested;

    /// <summary>
    /// Raised the moment this strip's drag goes live -- the pass where the
    /// row lifts and the follow expression starts. The horizontal strip's
    /// run label listens through the window: a drag anywhere that shares
    /// the drag surface must close the label in that same pass, so the
    /// label can never overlap a drag ghost it does not belong to.
    /// </summary>
    internal event Action? DragVisualStarted;

    /// <summary>
    /// The live drag ended, raised once from EndDrag -- the funnel every
    /// exit (drop, cancel, teardown) passes through. The counterpart of
    /// DragVisualStarted: the window listens so the horizontal run label's
    /// machine can stop refusing shows.
    /// </summary>
    internal event Action? DragVisualEnded;

    /// <summary>
    /// The command entry for collapse/expand: the router lands here (via
    /// the host) after announcing is guaranteed. Same drag stand-down as
    /// the chevron, and the explicit target state means a same-state
    /// command is a no-op rather than an accidental flip. Collapsing can
    /// hide the row that holds keyboard focus, so the re-home runs only
    /// on the collapse arm -- expanding hides nothing.
    /// </summary>
    internal void ToggleGroupFromCommand(TabGroup group, bool collapsed)
    {
        if (_drag is not null) return;
        if (group.IsCollapsed == collapsed) return;
        if (collapsed) RestoreFocusUnder(group);
        ToggleGroup(group);
    }

    /// <summary>
    /// Resolve the TabGroup for a hit-test target: the header row's Tag.
    /// Body rows and pinned rows carry TabModel on the same slot, so this
    /// answers null for them and the host falls through to the tab menu.
    /// </summary>
    internal TabGroup? GroupFromElement(DependencyObject? source) =>
        VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source)?.Tag as TabGroup;

    /// <summary>
    /// The first body row a keyboard crossing can land on: a visible
    /// top-level TAB. Both filters are load-bearing: headers are top-level
    /// too, and hidden collapsed members sit ahead of the visible one.
    /// </summary>
    private NavigationViewItem? FirstBodyItem() =>
        NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(
            i => i.Tag is TabModel && i.Visibility == Visibility.Visible);

    /// <summary>
    /// Bring both containers' rows back to the projection's: order in
    /// place (elements moved, never rebuilt, so a drag survives), collapse
    /// as visibility (the active member lands under its header -- Edge-135),
    /// header contents re-read. Membership skew is the rebuild's job.
    /// </summary>
    private void ReconcileRowOrder()
    {
        var pinCount = _manager.PinCount;
        // Desired MenuItems sequence and the visible set, from ONE walk of
        // the projection: headers at their run's start, rows in tab order,
        // pinned items skipped (the shelf renders them).
        var desired = new List<NavigationViewItem>(NavView.MenuItems.Count);
        var shown = new HashSet<TabModel>();
        var missing = false;
        foreach (var projected in TabStripProjection.GroupedRows(_manager))
        {
            switch (projected)
            {
                case TabStripProjection.ProjectedRow.Header { Group: { } group }:
                    if (!_headers.TryGetValue(group, out var header))
                    {
                        missing = true;
                        break;
                    }
                    desired.Add(header);
                    break;
                case TabStripProjection.ProjectedRow.Item { Tab: { } tab }:
                    if (tab.IsPinned) continue; // the shelf renders the prefix
                    shown.Add(tab);
                    if (!_items.TryGetValue(tab, out var item))
                    {
                        missing = true;
                        break;
                    }
                    desired.Add(item);
                    break;
            }
            if (missing) break;
        }

        // A projection-named row the strip holds no element for is skew an
        // order pass cannot repair -- counts can agree while a row is
        // missing on both sides -- so the miss flag joins the counts.
        //
        // The body-row expectation is `shown.Count`, the projection's own
        // rendered-item count, and NOT tabs-minus-pinned: a chip'd run
        // hides members on purpose, and they render no row. Expecting
        // tabs-minus-pinned here contradicted the rebuild's own output
        // (it removes the hidden member, dropping the count below the
        // formula), so every pass re-detected drift and re-ran
        // RebuildAllItems -- a dispatcher-looped rebuild that spun the UI
        // thread and ballooned the working set. The check must describe
        // what the rebuild produces, or the rebuild can never converge.
        if (missing
            || _pinnedRows.Count != pinCount
            || _items.Count != shown.Count
            || _pinnedPanel.Children.Count != pinCount
            || NavView.MenuItems.Count != desired.Count)
        {
            // The rebuild may land inside MUXC's still-open container
            // realization -- with virtualized hosts that state spans
            // frames. The retry yields off the foreign frame; every
            // attempt re-reads manager truth at run time.
            ReconcileRetry.Rebuild(
                "vertical rebuild",
                RebuildAllItems,
                SyncSelectionFromManager,
                m => TabDragTrace.Line($"DIAG {m}"),
                next => global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
                    global::Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                    () => next()));
            return;
        }

        for (var i = 0; i < pinCount; i++)
        {
            var tab = _manager.Tabs[i];
            var row = _pinnedRows[tab];
            if (ReferenceEquals(_pinnedPanel.Children[i], row)) continue;
            var idx = _pinnedPanel.Children.IndexOf(row);
            if (idx >= 0) _pinnedPanel.Children.RemoveAt(idx);
            _pinnedPanel.Children.Insert(i, row);
        }

        var items = NavView.MenuItems;
        _syncing = true;
        try
        {
            for (var i = 0; i < desired.Count; i++)
            {
                if (ReferenceEquals(items[i], desired[i])) continue;
                var idx = items.IndexOf(desired[i]);
                if (idx >= 0) items.RemoveAt(idx);
                items.Insert(i, desired[i]);
            }
        }
        finally { _syncing = false; }

        // Which rows show: the projection's items, pinned ones excluded
        // (they live in the shelf; their list entries are their visibility
        // anyway -- there are none). This assignment is the whole toggle.
        foreach (var (tab, item) in _items)
        {
            var want = shown.Contains(tab) ? Visibility.Visible : Visibility.Collapsed;
            if (item.Visibility != want) item.Visibility = want;
            ApplyRowInsets(item, tab);
        }

        foreach (var (group, header) in _headers)
        {
            if (header.Content is VerticalTabGroupHeaderRow row)
                row.Refresh(group, _manager.MembersOf(group).Count);
            ApplyGroupChrome(header, group);
            ApplyHeaderAnatomy(header);
        }

        UpdatePinnedShelfChrome();
    }

    /// <summary>
    /// The band's two state-dependent bits: it exists only while pins do,
    /// and it glides its squares between slots only while no gesture owns
    /// their composition Translation.
    ///
    /// There is no zone rule to gate any more. The band's own shape is
    /// what says where the pinned zone ends, at every pane width and
    /// whether or not a body row exists below it, so the "both zones
    /// exist" question the stroke had to ask no longer has an answer
    /// anything draws.
    /// </summary>
    private void UpdatePinnedShelfChrome()
    {
        var anyPins = _manager.PinCount > 0;
        _pinnedShelf.Visibility = anyPins ? Visibility.Visible : Visibility.Collapsed;

        // One writer on Translation. A live drag glides the rows it moves
        // -- pinned squares included -- and the band standing down for the
        // length of the gesture is what keeps the two from fighting.
        _pinnedPanel.MotionEnabled =
            _drag is null && TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast);
    }

    /// <summary>
    /// Re-answer every width-dependent question in the strip. The body
    /// rows and the group headers degrade at the same threshold, and the
    /// pinned band re-columns on its own arrange; a width change is the
    /// only event that moves all three at once -- the per-row passes that
    /// follow a pin, a group or a reorder each carry their own row.
    /// </summary>
    private void ApplyPaneWidthAnatomy()
    {
        UpdatePinnedShelfChrome();
        foreach (var (tab, item) in _items)
            ApplyRowInsets(item, tab);
        foreach (var (_, header) in _headers)
            ApplyHeaderAnatomy(header);
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

    /// <summary>
    /// The activation a shelf row gets, from a click (the drag machine's
    /// sub-threshold release) or from Enter/Space. The pinned panel is
    /// outside MUXC selection, so no SelectionChanged will ever carry
    /// either gesture to the manager: this is the same fenced activation
    /// the selection handler runs, spoken for the rows it cannot hear.
    /// </summary>
    private void ActivateFromShelf(TabModel tab)
    {
        if (ReferenceEquals(tab, _manager.ActiveTab)) return;

        _syncing = true;
        try { _manager.Activate(tab); }
        finally { _syncing = false; }

        ApplyAllItemTabColors();
        RecolorNavItems();
        RefreshSelectionChrome();
    }

    private void OnPinnedRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A drag owns the keyboard (Escape cancels it) and a drag never
        // activates, so shelf keys stand down while one is live.
        if (_drag is not null) return;
        if (sender is not VerticalTabPinnedRow { Tag: TabModel tab }) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space:
                e.Handled = true;
                ActivateFromShelf(tab);
                break;
            // Two axes, because the band has two. A step of one along the
            // row for Left/Right, a step of one COLUMN for Up/Down -- which
            // in a one-column compact rail is a step of one, so the rail
            // behaves exactly as the stack of rows it replaced did.
            case Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right:
                e.Handled = FocusShelfNeighbour(
                    tab, e.Key == Windows.System.VirtualKey.Right ? 1 : -1);
                break;
            case Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up:
                var stride = Math.Max(1, _pinnedPanel.Columns);
                e.Handled = FocusShelfNeighbour(
                    tab, e.Key == Windows.System.VirtualKey.Down ? stride : -stride);
                break;
        }
    }

    /// <summary>
    /// Walk the band by <paramref name="delta"/> slots, and cross the
    /// boundary at its edges. Panel order IS projection order
    /// (ReconcileRowOrder keeps them equal), so an index step is a real
    /// step through the pinned prefix.
    ///
    /// The CALLER decides what a step is worth, because the band wraps: a
    /// step of one is the square beside this one, and a step of one column
    /// is the square below it. Walking Down by one was correct while the
    /// pins were a vertical stack and became a lie the moment they shared a
    /// row -- Down moved the focus 44px to the RIGHT, and Left and Right
    /// did nothing at all, since the squares sit outside MUXC's traversal
    /// and nothing else was listening.
    ///
    /// Down past the last square lands on the first body row, where MUXC's
    /// own arrow traversal takes over. A downward step from the last band
    /// row lands past the end whatever the column count is, which is what
    /// makes that crossing survive the stride. Up past the first stops --
    /// the pane toggle above is MUXC's.
    /// </summary>
    private bool FocusShelfNeighbour(TabModel tab, int delta)
    {
        if (RowElementOf(tab) is not { } row) return false;
        var i = _pinnedPanel.Children.IndexOf(row) + delta;
        if (i >= 0 && i < _pinnedPanel.Children.Count)
            return _pinnedPanel.Children[i].Focus(FocusState.Programmatic);

        // A partly-filled last band row: Down from a square with no square
        // under it should still leave the band rather than stop dead, so an
        // overshoot crosses the same way a step past the end does. An
        // undershoot does not -- Up from the first row has nowhere to go.
        if (delta > 0 && FirstBodyItem() is { } firstBody)
            return firstBody.Focus(FocusState.Programmatic);

        return false;
    }

    private void OnBodyRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A drag owns the keyboard (Escape cancels it), here too.
        if (_drag is not null) return;
        // The one seam into the shelf from the body: Up from the FIRST
        // body row, only while the shelf exists. Everything else -- every
        // other key, every other row, no pins at all -- is MUXC's arrow
        // traversal, which must stay exactly as it was.
        if (e.Key != Windows.System.VirtualKey.Up) return;
        if (_manager.PinCount == 0) return;
        if (sender is not NavigationViewItem) return;
        if (FirstBodyItem() is not { } first || !ReferenceEquals(first, sender)) return;
        if (_pinnedPanel.Children.Count == 0) return;
        if (!_pinnedPanel.Children[^1].Focus(FocusState.Programmatic)) return;
        e.Handled = true;
    }

    // The shelf row under the pointer, if any. Enter/exit pairs are
    // trusted: a drag holds capture and suppresses them, so nothing has
    // to re-derive hover from drag state.
    private VerticalTabPinnedRow? _hoveredShelfRow;

    private void OnShelfRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not VerticalTabPinnedRow row) return;
        _hoveredShelfRow = row;
        PaintShelfRow(row);
    }

    private void OnShelfRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not VerticalTabPinnedRow row) return;
        if (!ReferenceEquals(_hoveredShelfRow, row)) return;
        _hoveredShelfRow = null;
        PaintShelfRow(row);
    }

    private void OnPinnedRowFocusVisual(object sender, RoutedEventArgs e)
    {
        if (sender is VerticalTabPinnedRow row) PaintShelfRow(row);
    }

    /// <summary>
    /// One painter for the shelf row's two pointer-adjacent states, focus
    /// and hover, on the pane's own hover-fill resource -- the same fill a
    /// body row's hover carries. Focus wins when both apply, so a
    /// keyboard user's indicator never flickers off because the pointer
    /// wandered across; the rest state is transparent, the lane the rows
    /// sit on.
    /// </summary>
    private void PaintShelfRow(VerticalTabPinnedRow row)
    {
        row.Background =
            row.FocusState != FocusState.Unfocused
            || ReferenceEquals(_hoveredShelfRow, row)
                ? ResolveThemeBrush("SubtleFillColorSecondaryBrush")
                : TransparentBrush;
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
    /// The strip's half of the shared drag oracle. Lines pair DRAG
    /// begin/end, one per commit, and the ghosts counts report
    /// composition the strip still believes it is driving -- the oracle
    /// reads any N above zero as a leak, and a `drop` line without a
    /// later `DRAG settle` as a settle that never completed.
    /// </summary>
    private static void DragTrace(string message) => TabDragTrace.Line(message);

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
        // A header drag drags a RUN: Tab is the run's first member (the
        // identity the churn and close guards already speak), Group names
        // the run, and null here means the ordinary one-row drag.
        public TabGroup? Group;
        // The run's visible member rows riding the header's follow. Hidden
        // members are never in here: they have no arranged geometry, and
        // a row translated to a slot it does not paint is a ghost. List
        // containers only, like VisibleRunRows builds.
        public readonly List<NavigationViewItem> CoDragRows = new();
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
        // The press X, for the one gesture that is not confined to the
        // strip's axis: a reorder inside a band row is pure sideways
        // travel. See DragMove's lift.
        public double PressX;
        public double PressBaseCenter;
        // The arranged center the anchor currently assumes; the tick's
        // measurement is only believed when it moves off this.
        public double AssumedCenter;
        public double LastPointerY;
        // The pointer's X, carried only for the pinned band. Everything
        // else in this gesture speaks one axis -- the machine's whole
        // contract is a scalar along it -- and the band is the one surface
        // that wraps, so it is the one place a second axis has to be
        // answered. Nothing outside BandTargetSlot reads this.
        public double LastPointerX;
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
        // No PointerCaptureLost hook, on purpose: the engine holds no
        // capture, so no CaptureLost is ours. The one this strip DOES see
        // is MUXC's own item layer releasing its press capture the moment
        // a drag starts moving -- acted on, it murders every real drag
        // (the probe caught exactly that: a cancel mid-drag, then a
        // zombie crossing landing the right order by luck).
        AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(OnDragKeyDown), true);
    }

    private void OnDragPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        DragPress(e.OriginalSource as DependencyObject, e.Pointer.PointerId,
            point.Y, point.X);
    }

    /// <summary>
    /// The press, parameterized on where the values come from. The pointer
    /// handler resolves the press origin, the pointer id, and the strip-space
    /// Y out of <see cref="PointerRoutedEventArgs"/> -- which is
    /// non-constructible, so the test seam cannot synthesize one; the seam
    /// passes the same three values itself and lands here anyway. Everything
    /// from the row resolution down is the one gesture body either way.
    /// </summary>
    private void DragPress(DependencyObject? source, uint pointerId, double y, double x = double.NaN)
    {
        if (_drag is not null)
        {
            // Without capture a release the strip never saw -- the button
            // coming up off the strip -- leaves the session behind; the
            // next press ends it here rather than blocking every drag
            // after it.
            CancelDrag("stale");
        }
        // A layout switch stages through SetSelectionRowSuppressed; a
        // drag never starts under one.
        if (_selectionRowSuppressed) return;

        // A row is either a NavigationViewItem in the list or a pinned row
        // in the shelf; both carry the tab on Tag.
        var item = (FrameworkElement?)VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source)
                ?? (FrameworkElement?)VisualTreeHelperEx.FindAncestor<VerticalTabPinnedRow>(source);
        if (item is null) return;
        // The close button owns its own presses; no drag grows out of one.
        if (VisualTreeHelperEx.FindAncestor<Button>(source) is not null) return;

        // A press on a group header arms the run drag: the machine drags
        // the whole run as one unit, the header is the visual that
        // follows, and MoveGroup is the commit behind every crossing.
        // The click (no lift) falls through to ItemInvoked's toggle
        // exactly as before.
        if (item is VerticalTabGroupHeaderItem { Tag: TabGroup group })
        {
            ArmGroupDrag(group, item, pointerId, y, x);
            return;
        }

        if (item.Tag is not TabModel tab) return;
        if (RowElementOf(tab) is not { } owned || !ReferenceEquals(owned, item)) return;
        if (_manager.Tabs.Count < 2) return;

        var (_, managerIndex) = DragSlots();
        var machine = new TabDragReorder(managerIndex.Count, SlotIndexOf(managerIndex, tab));
        machine.Press(y);
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
            PointerId = pointerId,
            PressRow = owned,
            LastPointerY = y,
            LastPointerX = x,
            PressX = x,
            PressY = y,
        };
    }

    /// <summary>
    /// Arm the run drag on a header press. The machine's slots are the
    /// unit space (one unit per body run, DragUnits below), not the tab
    /// rows: a crossing swaps the run past an entire neighbouring run or
    /// lone tab, because landing between another group's header and its
    /// members would split a run the projector cannot render. The pinned
    /// prefix contributes no units, so the drag never even offers a
    /// crossing into the zone MoveGroup's clamp would refuse.
    /// </summary>
    private void ArmGroupDrag(TabGroup group, FrameworkElement header, uint pointerId, double y, double x)
    {
        var run = _manager.MembersOf(group);
        if (run.Count == 0) return;
        var units = TabGroupDragUnits.Build(_manager);
        if (units.Count < 2) return;
        int grabbed = RunUnitIndex(units, group);
        if (grabbed < 0) return;

        var machine = new TabDragReorder(units.Count, grabbed);
        machine.Press(y);
        // Each begin..settle pair owns its census: a teardown failure in
        // one drag must not inflate the ghost count of the next.
        _teardownFailures = 0;
        _drag = new DragSession
        {
            Tab = run[0],
            Group = group,
            Machine = machine,
            PreDragOrder = TabStripProjection.Rows(_manager),
            PreDragPinned = new HashSet<TabModel>(
                _manager.Tabs.Where(t => t.IsPinned)),
            PointerId = pointerId,
            PressRow = header,
            LastPointerY = y,
            LastPointerX = x,
            PressX = x,
            PressY = y,
        };
    }

    /// <summary>
    /// The dragged unit's slot, found by IDENTITY. The manager-index
    /// arithmetic that DragSlots needs an inverse for answers a different
    /// unit's slot here -- the same 5b-1 lesson one level up -- so the
    /// unit space is matched by group, never by index math.
    /// </summary>
    private static int RunUnitIndex(IReadOnlyList<GroupDragUnit> units, TabGroup group)
    {
        for (int i = 0; i < units.Count; i++)
            if (ReferenceEquals(units[i].Group, group)) return i;
        return -1;
    }

    private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        DragMove(e.Pointer.PointerId, point.Y, point.X);
    }

    /// <summary>
    /// The move, parameterized the same way as the press.
    ///
    /// <paramref name="x"/> is the band's axis and nothing else's. It does
    /// NOT reach <see cref="TabDragReorder.Begin"/>: the start threshold is
    /// deliberately one-axis, so a grab that jitters sideways stays a
    /// click, and admitting X there would start a drag on a hand tremor.
    /// </summary>
    private void DragMove(uint pointerId, double y, double x = double.NaN)
    {
        if (_drag is not { } drag || pointerId != drag.PointerId) return;

        if (drag.Machine.Phase == TabDragPhase.Pressed)
        {
            // A press on a band square lifts on travel in EITHER direction,
            // and it has to: the band wraps, so a reorder between two
            // squares on one row is pure sideways travel at an unchanging
            // Y, and the one-axis threshold would keep that gesture a click
            // no matter how far it went.
            //
            // Only for a square. A body row keeps the one-axis rule, so a
            // hand tremor across a click still cannot lift it.
            // PER-AXIS, not the straight-line distance. A disc of radius
            // 4 is SMALLER than the +/-4 square the one-axis rule left a
            // click: 3px of horizontal and 3px of vertical travel is 4.24
            // away, so a click on a 40px icon with ordinary jitter lifted
            // -- and a lifted press that ends in the zone keeps its pin and
            // never activates, because only the click path calls
            // ActivateFromShelf. Taking the larger of the two axes is the
            // OR of two one-axis rules: the Y door is exactly what it was,
            // and X gets a door of the same width rather than the gesture
            // getting a smaller one overall.
            var lifted = drag.PressRow is VerticalTabPinnedRow
                         && !double.IsNaN(x) && !double.IsNaN(drag.PressX)
                ? drag.Machine.BeginOnTravel(
                    Math.Max(
                        Math.Abs(y - drag.PressY),
                        Math.Abs(x - drag.PressX)))
                : drag.Machine.Begin(y);
            if (!lifted) return;
            StartDragVisual(drag);
            if (_drag is null) return; // start refused; the click falls through
        }

        if (!double.IsNaN(x)) drag.LastPointerX = x;
        drag.LastPointerY = y;
        drag.Machine.SampleVelocity(y, Environment.TickCount64);
        drag.Properties?.InsertVector3("pointer", new Vector3(0, (float)y, 0));
        ScheduleDragEvaluate();
    }

    private void OnDragPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        DragRelease(e.Pointer.PointerId, e.GetCurrentPoint(this).Position.Y);
    }

    /// <summary>The release, parameterized the same way as the press.</summary>
    private void DragRelease(uint pointerId, double y)
    {
        if (_drag is not { } drag || pointerId != drag.PointerId) return;
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
            // ever carry the click to the manager -- ActivateFromShelf is
            // the fenced activation the selection handler runs, for the
            // rows it cannot hear. Body clicks keep flowing through MUXC.
            _drag = null;
            // The one exit that drops the session without EndDrag, so it
            // owes the dwell the same teardown: the invariant worth having
            // is "every path that nulls _drag clears the ring", not a
            // reachability argument that has to be redone whenever the
            // press path changes.
            ClearJoinDwell();
            if (drag.PressRow is VerticalTabPinnedRow)
                ActivateFromShelf(drag.Tab);
            return;
        }
        // HOLD WITH A RING: a release under a COMPLETED ring joins the
        // dragged row to the one it was held over. The ring is the whole
        // difference between the two meanings of this release, which is
        // why it is on screen for the entire wait and why the arm is read
        // from the dwell rather than re-derived from geometry here -- the
        // pointer may have drifted a pixel since, and the promise was
        // made to the row the ring was drawn on.
        if (_joinDwell.IsArmed && _joinDwell.Target is TabModel joinTarget)
        {
            TabGroup? group;
            _commitChurn = true;
            try
            {
                group = TabJoinDrop.Join(_manager, drag.Tab, joinTarget);
                DragTrace(group is null
                    ? "DRAG join refused"
                    : $"DRAG join group={group.Title} landed={_manager.IndexOf(drag.Tab)}");
            }
            finally { _commitChurn = false; }

            // Only a join that HAPPENED ends the gesture here. The arm says
            // what the release means; whether it could be honoured is the
            // manager's answer, and a refusal -- the target's shell exited
            // during the hold, so it is no longer in the manager -- leaves an
            // ordinary reorder to finish. Ending on the arm regardless cut the
            // settle spring the motion gate promises and skipped the pin arms
            // below, so a refused join was a drag that snapped home with no
            // settle and no group. The horizontal strip already keeps the
            // result and falls through; this is that rule.
            if (group is not null)
            {
                // The join's gather churns the dragged row's container, so
                // there is no live visual left for a settle spring to move:
                // the row lands as a cut in the slot the run gathered it into.
                EndDrag(drag, settle: false, velocity: 0);
                return;
            }
        }
        // PIN-OUT (release-classified): a row the drag pinned mid-gesture
        // ends where the user LET GO -- the same signal the horizontal
        // engine honors. Released over the shelf/zone: stay pinned (the
        // 4b-2 in-zone landing; the mid-drag crossings already placed the
        // row). Released out in the body: unpin and place at the body
        // slot under the release point. The position is fresh pointer
        // truth -- never machine centers, whose staleness after a pin is
        // the trap this replaces.
        if (drag.Tab.IsPinned)
        {
            var releaseY = y;
            // The boundary is the arbitration's own, and there is only one.
            // The tick loop hands the band every pointer above ShelfBottomY
            // and pins an arriving row the moment it gets one, so a release
            // that asked a different edge could only overturn what the drag
            // had already committed. The panel's rect is that different
            // edge: it sits inset inside the shelf, which makes its top and
            // bottom margins exactly the strips where the band pinned a row
            // and this arm unpinned it again. No top bound either, for the
            // same reason -- above the shelf the band still answers, so
            // above the shelf the release must still keep.
            //
            // No honest bound reads NaN, which fails the gate and runs the
            // unpin arm. That polarity is deliberate -- unpin unless
            // provably in-zone -- and safe to act on without the bounds,
            // because BodySlotAtY reads the body pairing and never the
            // shelf's layout.
            var shelfBottom = ShelfBottomY();
            var inZone = !double.IsNaN(shelfBottom) && releaseY < shelfBottom;
            if (!inZone)
            {
                _commitChurn = true;
                try
                {
                    _manager.SetPinned(drag.Tab, false);
                    var bodySlot = BodySlotAtY(releaseY);
                    var from = _manager.IndexOf(drag.Tab);
                    if (bodySlot >= 0 && from >= 0 && from != bodySlot)
                        _manager.Move(from, bodySlot);
                    DragTrace($"DRAG unpin drop body={bodySlot} from={from}");
                }
                finally { _commitChurn = false; }
            }
            DragTrace("DRAG pin release kept" + (inZone ? " in zone" : " -> unpinned"));
            EndDrag(drag, settle: drag.MotionOn, velocity: 0);
            return;
        }
        // Drop inside the pinned zone pins (5.5). The preview is the
        // promise -- it shows only while the row is still unpinned and its
        // center is over the shelf -- so honouring it at release is the
        // same zone-grammar commit the tick loop runs: Classify names the
        // op, SetPinned relocates to the prefix's last slot (exactly where
        // the ghost sat), and the read-back traces what landed. The churn
        // has replaced the dragged row's element, so there is no live
        // visual to settle: the row lands as a cut, in the ghost's slot --
        // or, with motion on, as the flight below.
        if (_pinPreview is not null)
        {
            // Both flight endpoints are read BEFORE the commit: the start
            // from the arranged row plus the follow offset the eye holds,
            // the destination from the preview itself, which has been
            // sitting on the exact slot the drop is about to fill. After
            // SetPinned the churn has replaced both elements, and a
            // freshly inserted row has no arranged truth to measure.
            Rect? start = null, dest = null;
            if (drag.MotionOn)
            {
                start = DraggedRowRect(drag);
                if (_pinPreview is { } preview && preview.ActualWidth > 0)
                    dest = new Rect(
                        Canvas.GetLeft(preview), Canvas.GetTop(preview),
                        preview.Width, VerticalTabPinnedRow.RowHeight);
            }

            var zone = TabPinBoundary.Classify(
                drag.Tab.IsPinned, _manager.PinCount, _manager.Tabs.Count,
                _manager.PinCount - 1);
            if (zone.Op == TabPinZoneOp.Pin)
            {
                _commitChurn = true;
                try { _manager.SetPinned(drag.Tab, true); }
                finally { _commitChurn = false; }
                DragTrace(
                    $"DRAG pin drop landed={_manager.IndexOf(drag.Tab)} " +
                    $"to={zone.To}");
            }
            EndDrag(drag, settle: false, velocity: 0);
            if (start is { } from && dest is { } to)
                StartPinFlight(drag, from, to);
            return;
        }
        // Nothing is held: the engine captures no pointer, so this
        // release is just the gesture's last hover-routed event, arriving
        // with the session still live for the handler to finish.
        DragTrace($"DRAG drop index={index} velocity={velocity:0}");
        EndDrag(drag, settle: drag.MotionOn, velocity: velocity);
    }

    private void OnDragPointerCanceled(object sender, PointerRoutedEventArgs e)
        => DragCancel(e.Pointer.PointerId);

    private void DragCancel(uint pointerId)
    {
        if (_drag is not { } drag || pointerId != drag.PointerId) return;
        CancelDrag("canceled");
    }

    private void OnDragKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_drag is not { Machine.Phase: TabDragPhase.Dragging }) return;
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        CancelDrag("escape");
    }

    private void StartDragVisual(DragSession drag)
    {
        // A fresh gesture outranks a flight still in the air: one flight
        // at a time, and the new drag's churn must not share the shelf
        // with a ghost from the old one.
        FinishPinFlight("superseded");
        // The band hands its squares' Translation back before the drag
        // takes it: from here the gesture's own glide pass is the only
        // writer, and a band glide still easing home would be overwritten
        // mid-flight and leave the square parked off its slot.
        _pinnedPanel.StopMotion();
        // Existence is the rep row's answer for both kinds; the anchor --
        // the element the follow rides -- is the header for a run.
        if (RowElementOf(drag.Tab) is not { } row) { CancelDrag("closed"); return; }
        // No capture: the host refuses CapturePointer for every gesture
        // (human and injected alike -- the loop's settled finding), and
        // the machine runs on the hover-routed pointer events, which
        // provably arrive: presses arm end to end through this entry, the
        // same shape the horizontal engine shipped.

        var item = drag.Group is { } group && _headers.TryGetValue(group, out var header)
            ? header
            : row;
        drag.Item = item;
        drag.MotionOn = TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast);
        DragVisualStarted?.Invoke();
        // The selection row is parked while a drag could paint over it; a
        // run drag parks it when the active member is INSIDE the run, not
        // merely when it is the grabbed row.
        drag.HidesSelectionRow = drag.Group is { } g
            ? _manager.MembersOf(g).Contains(_manager.ActiveTab)
            : ReferenceEquals(drag.Tab, _manager.ActiveTab);
        if (drag.Group is null)
            drag.Machine.UpdateIndex(SlotIndexOf(DragSlots().ManagerIndex, drag.Tab));
        else
            drag.Machine.UpdateIndex(RunUnitIndex(TabGroupDragUnits.Build(_manager), drag.Group));
        try
        {
            if (drag.Group is null)
            {
                var (center, centers) = MeasureRows(drag.Tab);
                drag.PressBaseCenter = center;
                drag.AssumedCenter = center;
                drag.Machine.UpdateCenters(centers);
            }
            else
            {
                // The anchor arithmetic stays in HEADER coordinates -- the
                // header is the element the pointer drags -- while the
                // machine judges crossings in unit midpoints. The two
                // frames meet in the pointer delta, which both share.
                drag.PressBaseCenter = ElementCenterY(item);
                drag.AssumedCenter = drag.PressBaseCenter;
                drag.Machine.UpdateCenters(MeasureUnitMids(drag));
            }
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
            if (drag.Group is not null) AttachCoDrag(drag);
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
            $"rows={_manager.Tabs.Count} run={(drag.Group is null ? "no" : "yes")} " +
            $"motion={(drag.MotionOn ? "on" : "off")}");
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
        if (RowElementOf(drag.Tab) is not { } row) return;
        // A run drag's follow lives on the header -- the one element the
        // churn does not rebuild -- but the header's MenuItems slot moved,
        // so the re-anchor below is what keeps it glued to the pointer.
        var item = drag.Group is { } group && _headers.TryGetValue(group, out var header)
            ? header
            : row;
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
        // The commit churned the member containers too: every rebuilt row
        // lost its translation and its accent, so the stack re-arms or it
        // tears -- members sitting at their layout slots while the header
        // rides the pointer.
        if (drag.Group is not null) AttachCoDrag(drag);
    }

    /// <summary>
    /// Re-feed the machine's slot centers from layout, keeping any slot
    /// the strip cannot measure right now at its previous value. Centers
    /// are read in the strip's current frame, so scrolling between here
    /// and the drag's start cannot skew a crossing threshold.
    /// </summary>
    private void RemeasureCenters(DragSession drag)
    {
        var (rows, _) = DragSlots();
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

    // -----------------------------------------------------------------
    // The run drag (5b-3): a header press drags the whole group as one
    // unit. The machine is the PR 3 machine unchanged -- it still speaks
    // slots, thresholds, and crossings -- but its slot list here is the
    // unit space (TabGroupDragUnits: one unit per body run), the dragged
    // center is the run's visible-span midpoint, and a crossing commits
    // through MoveGroup with a whole-run target. Hidden members never
    // enter any of it: they have no arranged geometry, and collapse
    // stays exactly as the user left it (Chrome's contract, not Edge's
    // grab-expands bug).
    // -----------------------------------------------------------------

    /// <summary>
    /// Arranged unit midpoints for the machine. The dragged unit's
    /// midpoint is the dragged center's arranged half; an unreadable one
    /// refuses the lift (the gesture degrades to the click it started
    /// as), matching MeasureRows' contract for the one-row drag.
    /// </summary>
    private double[] MeasureUnitMids(DragSession drag)
    {
        var units = TabGroupDragUnits.Build(_manager);
        var mids = new double[units.Count];
        double draggedMid = double.NaN;
        for (int i = 0; i < units.Count; i++)
        {
            mids[i] = UnitCenter(units[i], out _);
            if (drag.Group is { } g && ReferenceEquals(units[i].Group, g))
                draggedMid = mids[i];
        }
        if (double.IsNaN(draggedMid))
            throw new InvalidOperationException(
                "drag row is not realized; crossings cannot be judged");
        return mids;
    }

    /// <summary>
    /// One unit's arranged midpoint and visible span in strip
    /// coordinates. The head is the header for a group, the row itself
    /// for a lone tab, and the span reaches over the visible member rows
    /// only -- collapse-as-visibility means the visible rows are
    /// adjacent, so the span is the geometry a swap actually shifts.
    /// Only a failed HEAD read is NaN: unreadable tail rows are skipped,
    /// and the unit reports a partial-span midpoint rather than no
    /// crossing opinion this tick.
    /// </summary>
    private double UnitCenter(GroupDragUnit unit, out double span)
    {
        span = 0;
        FrameworkElement? head;
        List<NavigationViewItem> tail;
        if (unit.Group is { } group)
        {
            head = _headers.TryGetValue(group, out var h) ? h : null;
            tail = head is null ? new List<NavigationViewItem>() : VisibleRunRows(group);
        }
        else
        {
            head = RowElementOf(unit.Rep);
            tail = new List<NavigationViewItem>();
        }
        if (head is null) return double.NaN;
        double top = ElementTopY(head);
        if (double.IsNaN(top)) return double.NaN;
        double bottom = top + head.ActualHeight;
        foreach (var memberRow in tail)
        {
            double rowTop = ElementTopY(memberRow);
            if (double.IsNaN(rowTop)) continue;
            bottom = Math.Max(bottom, rowTop + memberRow.ActualHeight);
        }
        span = bottom - top;
        return (top + bottom) / 2;
    }

    /// <summary>
    /// The run's visible member rows. Visibility is the projection's own
    /// rule (collapse hides members except the active one, Edge-135), so
    /// the drag moves exactly the stack the user can see and never
    /// measures a row that has no arranged geometry. List containers
    /// only: a member renders in the scrolling list, never in the shelf.
    /// </summary>
    private List<NavigationViewItem> VisibleRunRows(TabGroup group)
    {
        var rows = new List<NavigationViewItem>();
        foreach (var tab in _manager.MembersOf(group))
        {
            if (group.IsCollapsed && !ReferenceEquals(tab, _manager.ActiveTab)) continue;
            if (RowElementOf(tab) is NavigationViewItem item) rows.Add(item);
        }
        return rows;
    }

    private double ElementTopY(FrameworkElement item)
    {
        if (item.ActualHeight <= 0) return double.NaN;
        try
        {
            return item.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            return double.NaN;
        }
    }

    private double ElementCenterY(FrameworkElement item)
    {
        double top = ElementTopY(item);
        return double.IsNaN(top) ? double.NaN : top + item.ActualHeight / 2;
    }

    /// <summary>
    /// Bind every visible member row to the header's follow: the same
    /// expression over the same property set reads the same translation
    /// on each row, so the stack moves rigidly. The rows take no lift and
    /// no glide of their own -- they are cargo -- and wear the 1px accent
    /// that says so. Called again after every commit, because the churn
    /// rebuilds member containers and a rebuilt row has neither the
    /// translation nor the accent until this re-arms it.
    /// </summary>
    private void AttachCoDrag(DragSession drag)
    {
        drag.CoDragRows.Clear();
        if (drag.Group is null || drag.Follow is null) return;
        foreach (var row in VisibleRunRows(drag.Group))
        {
            ElementCompositionPreview.SetIsTranslationEnabled(row, true);
            ElementCompositionPreview.GetElementVisual(row)
                .StartAnimation("Translation", drag.Follow);
            (row.Content as VerticalTabNavRow)?.SetCoDragAccent(true, AccentBrush);
            drag.CoDragRows.Add(row);
        }
    }

    /// <summary>
    /// Hand the co-drag rows back: translation off lands each on its
    /// arranged slot, the accent comes off, and the parked selection
    /// overlay catches up through the same tail the header's handback
    /// runs.
    /// </summary>
    private void DetachCoDrag(DragSession drag)
    {
        foreach (var row in drag.CoDragRows)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(row);
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                ElementCompositionPreview.SetIsTranslationEnabled(row, false);
            }
            catch (Exception ex) when (IsLayoutReadFailure(ex))
            {
                _teardownFailures++;
            }
            (row.Content as VerticalTabNavRow)?.SetCoDragAccent(false, AccentBrush);
        }
        drag.CoDragRows.Clear();
    }

    /// <summary>
    /// The header's post-swap arranged center, for the re-anchor: the
    /// whole run shifts by the pivot unit's visible span, down when the
    /// run swapped past the pivot below, up when past the pivot above.
    /// Both sides are pre-commit arranged reads, the same frame the
    /// one-row drag's re-anchor uses. NaN keeps the current anchor, and
    /// the next tick's measurement confirms the estimate either way.
    /// </summary>
    private double RunLandingCenter(
        DragSession drag, IReadOnlyList<GroupDragUnit> units, int pivot, bool down)
    {
        if (drag.Item is null) return double.NaN;
        double head = ElementCenterY(drag.Item);
        double pivotMid = UnitCenter(units[pivot], out var pivotSpan);
        if (double.IsNaN(head) || double.IsNaN(pivotMid)) return double.NaN;
        return head + (down ? pivotSpan : -pivotSpan);
    }

    /// <summary>
    /// The run drag's tick: the tab path's skeleton -- re-index, re-feed,
    /// judge crossings, read the truth back after every commit -- with
    /// MoveGroup as the commit and the unit formulas as the only mapping
    /// between a crossing and a manager index. No pin work and no ghost:
    /// groups cannot be pinned, and the unit space never offers a
    /// crossing into the prefix the clamp would refuse.
    /// </summary>
    private void EvaluateRunDrag(DragSession drag)
    {
        var group = drag.Group!;
        if (_manager.MembersOf(group).Count == 0 || !_headers.ContainsKey(group))
        {
            CancelDrag("closed");
            return;
        }

        var units = TabGroupDragUnits.Build(_manager);
        int dragged = RunUnitIndex(units, group);
        if (dragged < 0) { CancelDrag("closed"); return; }
        drag.Machine.UpdateIndex(dragged);
        double[] mids;
        try
        {
            mids = MeasureUnitMids(drag);
        }
        catch (InvalidOperationException)
        {
            // A container the projection holds is not realized this
            // tick -- a mid-drag rebuild (config reload, session
            // restore, theme flip) that has not re-arranged yet. The
            // lift path refuses the gesture on this throw; a tick
            // mid-flight only drops the frame and keeps its belief, the
            // same no-crossing outcome the NaN keep below grants a
            // merely unmeasured unit. The next tick retries after
            // relayout.
            return;
        }
        // A unit the strip cannot measure keeps its previous belief, the
        // same keep RemeasureCenters grants an unreadable row: Evaluate
        // treats NaN as no crossing and the next tick re-feeds it.
        for (int i = 0; i < mids.Length; i++)
            mids[i] = !double.IsNaN(mids[i]) || i >= drag.Machine.RowCount
                ? mids[i]
                : drag.Machine.CenterOf(i);
        drag.Machine.UpdateCenters(mids);
        double mid = mids[dragged];
        if (double.IsNaN(mid))
        {
            // The dragged run has no arranged truth this tick; an
            // unreadable measurement is no promise, and the anchor holds.
            return;
        }
        var draggedCenter = mid + (drag.LastPointerY - drag.AnchorY);

        // Pre-commit unit frame: the glides and the landing estimate both
        // read their deltas out of it.
        var beforeUnits = units;
        var beforeMids = mids;

        var committed = false;
        while (drag.Machine.Evaluate(draggedCenter) is { } crossing)
        {
            int pivot = crossing.To;
            if (pivot < 0 || pivot >= units.Count)
            {
                drag.Machine.UpdateIndex(pivot);
                DragTrace($"DRAG unmapped {crossing.From}->{crossing.To}");
                break;
            }
            bool down = crossing.To > crossing.From;
            var target = down
                ? TabGroupDragUnits.TargetAfter(units, units[dragged], pivot)
                : TabGroupDragUnits.TargetBefore(units, pivot);
            var landing = RunLandingCenter(drag, units, pivot, down);
            _commitChurn = true;
            try { _manager.MoveGroup(group, target); }
            finally { _commitChurn = false; }
            if (!_manager.Tabs.Contains(drag.Tab)) { CancelDrag("closed"); return; }

            // MoveGroup clamps, so read the truth back: a crossing that
            // did not land must not re-anchor the run to a slot it never
            // reached.
            var nowUnits = TabGroupDragUnits.Build(_manager);
            int now = RunUnitIndex(nowUnits, group);
            if (now < 0 || nowUnits[now].First != target)
            {
                if (now >= 0) drag.Machine.UpdateIndex(now);
                DragTrace($"DRAG refused {crossing.From}->{crossing.To}");
                break;
            }
            drag.Machine.UpdateIndex(now);
            units = nowUnits;
            dragged = now;
            RebindFollow(drag, landing);
            committed = true;
            DragTrace($"DRAG commit {crossing.From}->{crossing.To}");
        }

        StartRunGapGlides(beforeUnits, beforeMids, group);

        // Same adoption rule as the tab path, in the anchor's own frame:
        // a measurement that drifted off the header's assumed center is
        // layout truth catching up, and is adopted only on a tick with no
        // commit -- after a commit the stale pre-arrange read is exactly
        // what the anchor already modeled.
        if (!committed && drag.Item is not null)
        {
            double measured = ElementCenterY(drag.Item);
            if (!double.IsNaN(measured) && Math.Abs(measured - drag.AssumedCenter) > 0.5)
            {
                drag.AssumedCenter = measured;
                if (drag.Properties is not null) ApplyAnchor(drag, drag.Properties);
            }
        }

        // The run drag's tick answers the join question too, and its
        // answer is always no -- the guard is in UpdateJoinDwell, which is
        // where the rule lives for both strips. Reached from here rather
        // than left unwired: a tick that never asks is a tick that cannot
        // stand a ring DOWN, and the layout switch, a mid-drag rebuild, or
        // a later caller could each leave one up over a run.
        UpdateJoinDwell(drag, draggedCenter);
    }

    /// <summary>
    /// Displaced units' visible rows glide to their new slots, exactly as
    /// the one-row drag glides displaced rows: the delta is measured
    /// between pre-commit arranged midpoints of the two unit slots, so
    /// scroll motion cancels out of it. The dragged run's rows are
    // skipped -- they are the follow's cargo -- and a hidden member
    /// never glides, because an invisible row animating to a slot it
    /// does not paint is a ghost.
    /// </summary>
    private void StartRunGapGlides(IReadOnlyList<GroupDragUnit> beforeUnits,
        IReadOnlyList<double> beforeMids, TabGroup dragged)
    {
        if (_drag is not { } drag || !drag.MotionOn) return;
        if (drag.Glide is null || drag.Visual is null) return;

        var afterUnits = TabGroupDragUnits.Build(_manager);
        CompositionScopedBatch? batch = null;
        for (int i = 0; i < afterUnits.Count; i++)
        {
            var unit = afterUnits[i];
            if (ReferenceEquals(unit.Group, dragged)) continue;
            int old = IndexOfUnit(beforeUnits, unit);
            if (old < 0 || old == i) continue;
            if (old >= beforeMids.Count || i >= beforeMids.Count) continue;
            double delta = beforeMids[i] - beforeMids[old];
            if (double.IsNaN(delta) || Math.Abs(delta) < 0.5) continue;

            batch ??= drag.Visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            GlideUnit(unit, delta, batch);
        }
        // Completed lands after the glide's 250ms; only units still
        // riding THIS batch are handed back, so a re-glide inside the
        // window is not killed by the batch it superseded.
        if (batch is not null)
        {
            var settled = batch;
            settled.Completed += (_, _) =>
            {
                foreach (var entry in _gapMotion.Where(g => ReferenceEquals(g.Value.Batch, settled))
                             .ToList())
                    HandBackRow(entry.Key);
                foreach (var entry in _gapMotionHeaders
                             .Where(g => ReferenceEquals(g.Value.Batch, settled)).ToList())
                    HandBackHeader(entry.Key);
                DragTrace($"DRAG glide ghosts={CountLeakedMotion()}");
            };
            settled.End();
        }
    }

    private static int IndexOfUnit(IReadOnlyList<GroupDragUnit> units, GroupDragUnit unit)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (unit.Group is null
                ? units[i].Group is null && ReferenceEquals(units[i].Rep, unit.Rep)
                : ReferenceEquals(units[i].Group, unit.Group))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// One displaced unit's glide: every visible row of the run shifts by
    /// the unit delta -- a unit is rigid -- with the header stored under
    /// its own leak census so the ghost count stays honest.
    /// </summary>
    private void GlideUnit(GroupDragUnit unit, double delta, CompositionScopedBatch batch)
    {
        if (_drag is not { } drag) return;
        if (unit.Group is { } group)
        {
            if (_headers.TryGetValue(group, out var header))
                GlideHeader(group, header, drag, delta, batch);
            foreach (var tab in VisibleRunRowTabs(group))
                GlideRow(tab, delta, batch);
        }
        else if (RowElementOf(unit.Rep) is not null)
        {
            GlideRow(unit.Rep, delta, batch);
        }
    }

    private List<TabModel> VisibleRunRowTabs(TabGroup group)
    {
        var tabs = new List<TabModel>();
        foreach (var tab in _manager.MembersOf(group))
        {
            if (group.IsCollapsed && !ReferenceEquals(tab, _manager.ActiveTab)) continue;
            if (RowElementOf(tab) is not null) tabs.Add(tab);
        }
        return tabs;
    }

    private void GlideHeader(
        TabGroup group, FrameworkElement item, DragSession drag, double delta,
        CompositionScopedBatch batch)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(item);
            visual.StopAnimation("Translation");
            ElementCompositionPreview.SetIsTranslationEnabled(item, true);
            visual.Properties.InsertVector3("Translation", new Vector3(0, (float)-delta, 0));
            if (drag.Glide is not null)
                visual.StartAnimation("Translation", drag.Glide);
            _gapMotionHeaders[group] = (item, batch);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            // Composition refused: this row lands as a cut, like the
            // motion-off path. State never depends on the glide.
        }
    }

    private void HandBackHeader(TabGroup group)
    {
        if (!_gapMotionHeaders.Remove(group, out var entry)) return;
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
    // The same census for run glides, keyed by the displaced group: a
    // unit's header glides beside its member rows and needs its own
    // handback slot, or the ghost count under-counts exactly the run case.
    private readonly Dictionary<TabGroup, (FrameworkElement Item, CompositionScopedBatch Batch)>
        _gapMotionHeaders = new();
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
    /// the two slots involved, so scroll motion cancels out of it. Both
    /// sides are slot lists (visible rows only): a hidden member of a
    /// collapsed group has no arranged center to glide from, and an
    /// invisible row animating to a slot it does not paint is a ghost.
    /// </summary>
    private void StartGapGlides(IReadOnlyList<TabModel> beforeRows,
        IReadOnlyList<double> beforeCenters, TabModel dragged)
    {
        if (_drag is not { } drag || !drag.MotionOn) return;
        if (drag.Glide is null || drag.Visual is null) return;

        var (afterRows, _) = DragSlots();
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
        foreach (var group in _gapMotionHeaders.Keys.ToList())
            HandBackHeader(group);
        _gapMotionHeaders.Clear();
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

        // A header press drags a RUN: its tick is EvaluateRunDrag's, and
        // none of the one-row tick below applies to it -- no slot pairing
        // (the machine speaks unit midpoints) and no pin work (groups
        // cannot be pinned, so there is no ghost to promise either).
        if (drag.Group is not null)
        {
            EvaluateRunDrag(drag);
            return;
        }

        var (_, managerIndex) = DragSlots();
        drag.Machine.UpdateIndex(SlotIndexOf(managerIndex, drag.Tab));
        RemeasureCenters(drag);
        double arranged = RowCenterY(drag.Tab);
        if (double.IsNaN(arranged))
        {
            // The dragged row has no arranged truth this tick, so the
            // preview's gate cannot be evaluated: an unreadable
            // measurement is no promise, and a ghost left standing on a
            // stale one outlives its premise.
            HidePinPreview();
            return;
        }
        var draggedCenter = arranged + (drag.LastPointerY - drag.AnchorY);

        // Pre-commit arranged centers, one measurement per row: the gap
        // glides read their slot deltas out of this frame, so every delta
        // is scroll-consistent and layout-race-free (arrange cannot run
        // mid-tick).
        var (beforeRows, _) = DragSlots();
        var beforeCenters = new double[beforeRows.Count];
        for (int i = 0; i < beforeRows.Count; i++)
            beforeCenters[i] = RowCenterY(beforeRows[i]);

        var committed = false;
        // The band answers for itself while the pointer is over it, and the
        // crossing engine is not consulted at all on that tick.
        //
        // Not an optimisation -- the crossing engine cannot answer here and
        // is actively wrong when asked. It commits on the dragged centre
        // passing a NEIGHBOUR'S centre along one axis, and squares sharing a
        // band row share that centre: no crossing between them can ever be
        // produced, so a reorder inside a row was impossible. Worse, the
        // comparison that fires for one square on the row fires for every
        // square on it, and this loop drains crossings until none is left --
        // so a drag arriving at the band committed 2->1, re-evaluated
        // against the identical centre, committed 1->0, and landed at slot 0
        // whatever the pointer was pointing at.
        // ...and a PINNED row is never handed to it, band tick or not. The
        // paragraph above is only half true while the pointer is over the
        // band: carry a square below the shelf and BandTargetSlot declines,
        // which hands the crossing engine that same array of equal centres
        // and it drains three commits in one tick, shuffling the square to
        // slot 0 exactly as described. Below the shelf a pinned row's
        // position among body rows means nothing anyway -- it is still in
        // the band -- and where it ends up is the release's call, which
        // unpins and places it under the release point. So there is nothing
        // for a crossing to decide here, and letting it try is the bug.
        var bandTarget = BandTargetSlot(drag);
        if (drag.Tab.IsPinned && bandTarget is null)
        {
            // No reorder this tick. The tail still runs: the ghost, the
            // dwell and the glides are all still this gesture's business.
        }
        else if (bandTarget is { } bandSlot)
        {
            committed = CommitBandTarget(drag, bandSlot, beforeCenters);
            // CommitBandTarget can cancel the session, and the crossing arm
            // below returns on its own cancels rather than finishing the
            // tick. This owes the same: EndDrag has already hidden the
            // ghost and cleared the ring, and the tail of this method would
            // re-derive both from a torn-down session -- ShowPinPreview
            // adds a fresh row to PreviewHost that nothing then owns, and
            // CountLeakedMotion does not count it, so the oracle reports a
            // clean gesture while a phantom pin sits on the shelf.
            if (_drag is null) return;
        }
        else while (drag.Machine.Evaluate(draggedCenter) is { } crossing)
        {
            // The machine speaks in visible slots, the manager in tabs;
            // a slot the pairing does not name is refused, never guessed.
            var managerTo = crossing.To >= 0 && crossing.To < managerIndex.Count
                ? managerIndex[crossing.To]
                : -1;
            if (managerTo < 0)
            {
                drag.Machine.UpdateIndex(crossing.To);
                DragTrace($"DRAG unmapped {crossing.From}->{crossing.To}");
                break;
            }
            var from = _manager.IndexOf(drag.Tab);
            // A crossing over the pin boundary is a zone change Move alone
            // would clamp away: SetPinned first relocates the row to the
            // boundary, then the Move places it at the crossing's slot in
            // the new zone -- the drop position, not append-last. The arm
            // PINS only: pin-out is release-classified, so a mid-drag
            // Unpin classification is stale centers speaking, and it is
            // refused below rather than obeyed.
            var zone = TabPinBoundary.Classify(
                drag.Tab.IsPinned, _manager.PinCount, _manager.Tabs.Count, managerTo);
            _commitChurn = true;
            try
            {
                if (zone.Op == TabPinZoneOp.Pin)
                {
                    _manager.SetPinned(drag.Tab, true);
                    DragTrace($"DRAG pin {crossing.From}->{crossing.To}");
                    from = _manager.IndexOf(drag.Tab);
                    if (from < 0) { CancelDrag("closed"); return; }
                }
                else if (zone.Op == TabPinZoneOp.Unpin)
                {
                    // Mid-drag unpin is noise, never intent: nothing was
                    // committed, so the row still sits at crossing.From.
                    // Rewind the machine to it and refuse the crossing --
                    // no SetPinned, no Move; the release arm owns the out.
                    drag.Machine.UpdateIndex(crossing.From);
                    DragTrace($"DRAG refused {crossing.From}->{crossing.To}");
                    break;
                }
                _manager.Move(from, managerTo);
            }
            finally { _commitChurn = false; }
            if (zone.Op != TabPinZoneOp.None)
            {
                // The boundary is what this gesture aims at, so the one
                // commit that moves it repaints it now; ordinary moves keep
                // the deliberate mid-drag freeze.
                if (drag.HidesSelectionRow) UpdateSelectionRow();
                else UpdateRowSeparators(selectionRowVisible: true);
            }
            if (RowElementOf(drag.Tab) is null) { CancelDrag("closed"); return; }

            // Move clamps at the boundary and no-ops on collapse, so read
            // the truth back: a crossing that did not land must not
            // re-anchor the row to a slot it never reached. The pairing is
            // re-walked post-commit -- the move displaced other rows, so
            // the pre-commit pairing is stale past any hidden member.
            var (nowRows, nowIndex) = DragSlots();
            var actual = _manager.IndexOf(drag.Tab);
            var actualSlot = nowIndex.IndexOf(actual);
            if (actual != managerTo || actualSlot < 0)
            {
                if (actualSlot >= 0) drag.Machine.UpdateIndex(actualSlot);
                DragTrace($"DRAG refused {crossing.From}->{crossing.To}");
                break;
            }
            drag.Machine.UpdateIndex(actualSlot);
            managerIndex = nowIndex;
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

        UpdatePinPreview(drag);
        UpdateJoinDwell(drag, draggedCenter);
    }

    // -----------------------------------------------------------------
    // The join dwell: hold with a ring. While a body row is dragged and
    // comes to rest over a neighbour, a ring over that neighbour fills;
    // once it completes the row haloes and the release JOINS the two into
    // a group. A release before the ring fills is the ordinary sort the
    // crossings already committed, unchanged.
    //
    // Nothing here touches the crossing engine, and that is the design
    // rather than an omission. The dwell restarts on pointer travel, so
    // it can only complete over a pointer that has stopped -- and a
    // stopped pointer earns no further crossings, because the machine
    // judges a fixed dragged center. So there is no crossing to suppress
    // and no state the two gestures fight over: the ring only ever runs
    // in the gap the reorder has already gone quiet in.
    //
    // The clock is the strip's, not the machine's, because a hand held
    // perfectly still raises no pointer events at all: a repeating timer
    // is what advances the ring, and the seam swaps a virtual clock in so
    // the hold is a fact a test states rather than a race it hopes to
    // win.
    // -----------------------------------------------------------------

    private readonly TabJoinDwell _joinDwell = new();
    private DispatcherQueueTimer? _joinTimer;
    private TabJoinRing? _joinRing;

    /// <summary>
    /// The dwell's clock, in milliseconds. The wall clock in the product;
    /// the seam pins a virtual one for the length of one gesture, because
    /// a 450ms hold asserted against a loaded thread pool measures the
    /// scheduler rather than the ring.
    /// </summary>
    private long? _seamJoinClockMs;

    private long JoinClockMs => _seamJoinClockMs ?? Environment.TickCount64;

    /// <summary>
    /// Re-derive the ring from this tick's truth: which neighbour the
    /// dragged row is sitting on, whether joining it would actually do
    /// anything, and how full the ring is by now.
    ///
    /// Three states never ring at all. A run drag carries a whole group,
    /// and a run landing inside a run is a different op with its own
    /// grammar. A pinned row cannot be in a group, so the prefix outranks
    /// the promise. And a live pin preview already owns what the release
    /// means -- two promises over one release is how a gesture starts
    /// lying.
    /// </summary>
    private void UpdateJoinDwell(DragSession drag, double draggedCenter)
    {
        if (drag.Group is not null || drag.Tab.IsPinned || _pinPreview is not null)
        {
            ClearJoinDwell();
            return;
        }

        var (rows, _) = DragSlots();
        var machine = drag.Machine;
        // The machine's centres, not a fourth sweep of the strip.
        //
        // EvaluateDrag has already called DragSlots twice, built its own
        // beforeCenters with a full RowCenterY walk, and pushed a third walk
        // into the machine through RemeasureCenters -- three lines above this.
        // Measuring again meant ~30 more TransformToVisual calls and two
        // allocations per frame, at 60Hz, for the whole of every drag, to serve
        // a gesture that is live in a small fraction of them. The horizontal
        // strip reads machine.CenterOf(i); this is that, which also removes the
        // last place the two strips derived the mapping differently.
        if (rows.Count != machine.RowCount) { ClearJoinDwell(); return; }
        var centers = new double[machine.RowCount];
        for (int i = 0; i < centers.Length; i++) centers[i] = machine.CenterOf(i);

        int pick = TabJoinDrop.PickTarget(
            centers, machine.Index, draggedCenter, TabStripMotion.JoinBandFraction);
        // The ring never promises a join the release would refuse: the
        // same no-false-promise rule the pin ghost obeys.
        if (pick < 0 || !TabJoinDrop.CanJoin(_manager, drag.Tab, rows[pick]))
        {
            ClearJoinDwell();
            return;
        }

        _joinDwell.Hold(rows[pick], drag.LastPointerY, JoinClockMs);
        StartJoinTimer();
        UpdateJoinRing();
    }

    /// <summary>
    /// The timer's own pass: advance the ring over the target the last
    /// pointer tick picked, WITHOUT re-measuring. The pointer has not
    /// moved -- that is the premise of a dwell -- so re-deriving the
    /// target sixty times a second would answer the same question at the
    /// cost of a full row sweep per frame.
    /// </summary>
    private void TickJoinDwell()
    {
        if (_drag is not { } drag || _joinDwell.Target is not TabModel target)
        {
            ClearJoinDwell();
            return;
        }
        // Re-asked every tick, not only on the pointer path. The dwell's whole
        // premise is that no pointer event arrives for 450ms, so this is the
        // one window that decides the gesture -- and it was the one window in
        // which eligibility went unchecked. A target pinned by an accelerator
        // mid-hold, gathered into the dragged tab's own group by another actor,
        // or closed, armed a promise the release could no longer keep.
        if (!TabJoinDrop.CanJoin(_manager, drag.Tab, target))
        {
            ClearJoinDwell();
            return;
        }
        _joinDwell.Hold(target, drag.LastPointerY, JoinClockMs);
        UpdateJoinRing();
    }

    /// <summary>
    /// Draw the ring over the target's arranged row. An unreadable
    /// measurement withdraws the whole DWELL, not just the visual.
    ///
    /// "No ring is the honest picture" was only half of it: hiding the ring
    /// while the clock kept filling left the gesture arming with nothing on
    /// screen, so a release meant JOIN with no affordance ever having been
    /// shown. No ring plus a live promise is the dishonest picture -- and the
    /// PR's central claim is that the fill IS the dwell's progress, which is
    /// only true if the two are withdrawn together.
    /// </summary>
    private void UpdateJoinRing()
    {
        if (_joinDwell.Target is not TabModel target
            || RowElementOf(target) is not { } element
            || element.ActualHeight <= 0)
        {
            ClearJoinDwell();
            return;
        }
        Windows.Foundation.Point origin;
        try
        {
            origin = element.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            ClearJoinDwell();
            return;
        }

        if (_joinRing is null)
        {
            _joinRing = new TabJoinRing(AccentBrush);
            PreviewHost.Children.Add(_joinRing);
        }
        PreviewHost.Visibility = Visibility.Visible;
        _joinRing.Place(
            new Windows.Foundation.Rect(
                origin.X, origin.Y, element.ActualWidth, element.ActualHeight),
            _joinDwell.Progress,
            _joinDwell.IsArmed,
            _drag is { MotionOn: true },
            _highContrast);
    }

    /// <summary>
    /// The ring fills on a clock, not on pointer events: a hand held
    /// perfectly still raises none, and a ring that only advanced on
    /// motion could never complete.
    /// </summary>
    private void StartJoinTimer()
    {
        if (_joinTimer is not null) return;
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Interval = TimeSpan.FromMilliseconds(TabStripMotion.JoinRingTickMs);
        timer.Tick += (_, _) => TickJoinDwell();
        _joinTimer = timer;
        timer.Start();
    }

    /// <summary>
    /// Take the promise back. EndDrag is the one place every gesture exit
    /// funnels through, so an armed dwell cannot outlive the drag that
    /// armed it and be read by the next release.
    /// </summary>
    private void ClearJoinDwell()
    {
        _joinDwell.Clear();
        _joinTimer?.Stop();
        _joinTimer = null;
        HideJoinRing();
    }

    private void HideJoinRing()
    {
        if (_joinRing is null) return;
        _joinRing.Reset();
        PreviewHost.Children.Remove(_joinRing);
        _joinRing = null;
        if (PreviewHost.Children.Count == 0)
            PreviewHost.Visibility = Visibility.Collapsed;
    }

    // -----------------------------------------------------------------
    // The pin drop preview (5.5): while a body row is dragged over the
    // shelf and has not yet committed its crossing, an icon-only ghost
    // slot sits in the shelf at the position the drop would land. The
    // state machine is show/move/hide: shown and re-positioned from the
    // coalesced drag tick, hidden by EndDrag -- the shared tail of the
    // drop and every cancel -- and it never touches manager state. Once
    // the crossing DOES commit, the real icon-only row is in the shelf
    // following the pointer, and the ghost would promise a slot the real
    // row already holds; the gate below hides it on that tick.
    // -----------------------------------------------------------------

    /// <summary>
    /// Put the dragged tab in band slot <paramref name="target"/>: the
    /// band's whole commit, and it is ONE move rather than a drained run
    /// of crossings.
    ///
    /// That is the shape the two-axis hit test buys. A crossing engine has
    /// to walk a drag slot by slot because each step is a comparison
    /// against one neighbour; the band already knows the answer outright,
    /// so there is nothing to iterate and no way for the iteration to run
    /// away. <see cref="TabPinBand.NearestSlot"/> only changes its answer
    /// at the midpoint between two squares, so a pointer resting on a
    /// boundary cannot thrash the manager either.
    ///
    /// An unpinned tab arriving is pinned first and then placed, the same
    /// two steps the boundary crossing takes and for the same reason: Move
    /// alone clamps to the tab's own zone, so a pin has to exist before it
    /// can be positioned among the pins.
    ///
    /// Returns whether anything committed, which is what the caller's
    /// anchor-adoption rule reads.
    ///
    /// Of the three "closed" cancels below, only the last -- the row
    /// element being gone after the churn -- is live. EvaluateDrag has
    /// already proved the tab is in the manager, and SetPinned relocates
    /// rather than removes, so neither IndexOf can return -1. They stay as
    /// belt-and-braces on a path that mutates manager state mid-gesture,
    /// but do not read the method as having three failure modes.
    /// </summary>
    private bool CommitBandTarget(DragSession drag, int target, double[] beforeCenters)
    {
        var wasPinned = drag.Tab.IsPinned;
        var from = _manager.IndexOf(drag.Tab);
        if (from < 0) { CancelDrag("closed"); return false; }
        // The band's slots ARE the manager's pinned prefix, in order, so a
        // band slot is a manager index directly -- no pairing to walk. The
        // clamp is the arriving case: a tab that is not pinned yet cannot
        // land past the end of the prefix it is joining.
        var managerTo = Math.Min(target, wasPinned ? _manager.PinCount - 1 : _manager.PinCount);
        if (wasPinned && managerTo == from) return false;

        _commitChurn = true;
        try
        {
            if (!wasPinned)
            {
                _manager.SetPinned(drag.Tab, true);
                from = _manager.IndexOf(drag.Tab);
                if (from < 0) { CancelDrag("closed"); return false; }
            }
            if (from != managerTo) _manager.Move(from, managerTo);
        }
        finally { _commitChurn = false; }

        if (!wasPinned)
        {
            // The zone moved, so the boundary this gesture aims at repaints
            // now -- the same exception the crossing path makes for a pin.
            if (drag.HidesSelectionRow) UpdateSelectionRow();
            else UpdateRowSeparators(selectionRowVisible: true);
        }
        if (RowElementOf(drag.Tab) is null) { CancelDrag("closed"); return false; }

        // Read the truth back rather than trusting the request: Move clamps,
        // and a target the manager declined must not re-anchor the row to a
        // slot it never reached.
        var (_, nowIndex) = DragSlots();
        var actualSlot = nowIndex.IndexOf(_manager.IndexOf(drag.Tab));
        if (actualSlot < 0)
        {
            DragTrace($"BAND refused ->{target}");
            return false;
        }
        drag.Machine.UpdateIndex(actualSlot);
        // The anchor re-bases on where the square was BEFORE this move, so
        // the row keeps following the pointer instead of jumping by the
        // slot delta. Out of the pre-commit frame the caller measured, and
        // only when that frame actually holds the slot: a tab arriving from
        // the list was not in the band when those centers were read.
        if (actualSlot < beforeCenters.Length && !double.IsNaN(beforeCenters[actualSlot]))
            RebindFollow(drag, beforeCenters[actualSlot]);
        DragTrace($"BAND commit ->{actualSlot}");
        return true;
    }

    /// <summary>
    /// Re-derive the ghost from this tick's truth. The promise is only
    /// alive while the dragged row is still unpinned, pins exist, and the
    /// row's center is over the shelf; any other state hides it. An
    /// unreadable measurement also hides it -- a ghost at a stale position
    /// is a wrong promise, and no ghost is the honest one.
    /// </summary>
    private void UpdatePinPreview(DragSession drag)
    {
        // The POINTER's Y, not the dragged row's centre. BandTargetSlot
        // arbitrates on the pointer, and it acts on that answer by pinning
        // an arriving row the moment it gets one -- so a ghost gated on the
        // centre disagrees by the grab offset, up to half a row height.
        // Grab a body row near its top edge and there is a band of
        // positions where the band commits a pin while this refused to
        // promise one, which is the drop landing somewhere the user was
        // never shown. One reader of one number, or the promise is not a
        // promise.
        var shelfBottom = ShelfBottomY();
        if (drag.Tab.IsPinned || _manager.PinCount == 0
            || double.IsNaN(shelfBottom) || double.IsNaN(drag.LastPointerY)
            || drag.LastPointerY >= shelfBottom)
        {
            HidePinPreview();
            return;
        }

        // The slot the pointer is actually over, asked of the band itself
        // rather than derived here. A band wraps, so a slot is sometimes
        // beside the last square and sometimes at the start of a new row,
        // and only the panel that arranges the squares knows which --
        // re-deriving it would be a second layout opinion that disagrees at
        // every column boundary.
        //
        // The ghost and the landing are the SAME answer: both come from
        // BandTargetSlot, so the promise cannot differ from what the release
        // does. Falling back to the end slot when the pointer is not over
        // the band keeps the old promise for the approach, which is the
        // only time that gate is open and the band has not answered.
        var slot = BandSlotRect(BandTargetSlot(drag) ?? _manager.PinCount);
        if (double.IsNaN(slot.X) || double.IsNaN(slot.Y))
        {
            HidePinPreview();
            return;
        }

        ShowPinPreview(drag, top: slot.Y, left: slot.X, width: slot.Width);
    }

    /// <summary>
    /// A band slot's box in THIS control's coordinates, including the
    /// slot one past the last square. NaN when the band has not been
    /// arranged into the tree yet: a promise about a slot nobody has
    /// placed is a wrong promise, and the caller hides the ghost.
    /// </summary>
    private Windows.Foundation.Rect BandSlotRect(int index)
    {
        var nowhere = new Windows.Foundation.Rect(
            double.NaN, double.NaN, TabPinBand.ChipSize, TabPinBand.ChipSize);
        try
        {
            var slot = _pinnedPanel.SlotRect(index);
            var origin = _pinnedPanel.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(slot.X, slot.Y));
            return new Windows.Foundation.Rect(
                origin.X, origin.Y, slot.Width, slot.Height);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            return nowhere;
        }
    }

    private double ShelfBottomY()
    {
        try
        {
            return _pinnedShelf.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, _pinnedShelf.ActualHeight)).Y;
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// The band slot the pointer is asking for, or null when the pointer is
    /// not over the band and the linear machine owns the gesture.
    ///
    /// This is the arbitration between the two engines, and the axis it
    /// arbitrates on is the machine's own. The band spans the pane's width,
    /// so horizontally there is nothing to decide -- the only question is
    /// whether the pointer's Y is inside the band's rows. Above the shelf's
    /// bottom the band answers; below it the list does. Split that way the
    /// two engines cannot both claim a tick, which is what keeps the
    /// handover from needing a state machine of its own.
    ///
    /// Null on three refusals rather than a guess:
    ///   - no X. A driver that supplies only Y (the seam's Y-only drag, and
    ///     every pre-band caller) gets the linear behaviour it has always
    ///     had, instead of a hit test against a column it never named.
    ///   - no pins. There is no band to hit-test, and nothing else picks
    ///     the gesture up either: TabPinBoundary.Classify only answers Pin
    ///     for a slot inside the pinned prefix, and with no pins there is
    ///     no such slot. So no drag creates the FIRST pin, before this
    ///     change or after it -- the refusal is right, but do not read it
    ///     as "the boundary classification handles that case".
    ///   - a run drag. A group cannot be pinned, so the band is not a place
    ///     it can land.
    /// </summary>
    private int? BandTargetSlot(DragSession drag)
    {
        if (drag.Group is not null) return null;
        if (double.IsNaN(drag.LastPointerX)) return null;
        if (_manager.PinCount == 0) return null;

        var shelfBottom = ShelfBottomY();
        if (double.IsNaN(shelfBottom) || drag.LastPointerY >= shelfBottom) return null;

        Windows.Foundation.Point local;
        try
        {
            local = TransformToVisual(_pinnedPanel).TransformPoint(
                new Windows.Foundation.Point(drag.LastPointerX, drag.LastPointerY));
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            // The band is not arranged into the tree this tick. No target
            // is the honest answer; the linear machine's own measurements
            // are refused the same way one tick at a time.
            return null;
        }
        if (double.IsNaN(local.X) || double.IsNaN(local.Y)) return null;

        // One more slot than there are pins when the dragged tab is not one
        // of them: it is arriving, and the slot past the end is a real place
        // for it to land -- the same slot the ghost draws. A pin already in
        // the band is only ever reordered among the squares that exist.
        var slots = drag.Tab.IsPinned ? _manager.PinCount : _manager.PinCount + 1;
        return TabPinBand.NearestSlot(
            local.X, local.Y, Math.Max(1, _pinnedPanel.Columns), slots);
    }

    /// <summary>
    /// Build the ghost fresh for this promise: a real pinned row shape
    /// (the destination state, icon-only) at half strength, untouchable --
    /// no hit testing, no tab stop, and out of the raw accessibility view
    /// so a client never hears a tab that does not exist yet.
    /// </summary>
    private void ShowPinPreview(DragSession drag, double top, double left, double width)
    {
        if (_pinPreview is null)
        {
            var ghost = new VerticalTabPinnedRow(drag.Tab, AccentBrush)
            {
                IsHitTestVisible = false,
                IsTabStop = false,
                Opacity = 0.5,
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAccessibilityView(
                ghost, AccessibilityView.Raw);
            _pinPreview = ghost;
            PreviewHost.Children.Add(ghost);
            PreviewHost.Visibility = Visibility.Visible;
        }

        _pinPreview.SetIcon(TabIconElementFactory.Create(drag.Tab.TabIcon));
        _pinPreview.Width = width;
        Canvas.SetLeft(_pinPreview, left);
        Canvas.SetTop(_pinPreview, top);
    }

    /// <summary>
    /// Take the promise back. EndDrag is the one place: drop and every
    /// cancel family (escape, capture loss, row close, layout switch,
    /// teardown) tear the gesture down here, so the ghost cannot outlive
    /// the drag that promised it.
    /// </summary>
    private void HidePinPreview()
    {
        if (_pinPreview is null) return;
        PreviewHost.Children.Remove(_pinPreview);
        _pinPreview = null;
        PreviewHost.Visibility = Visibility.Collapsed;
    }

    // -----------------------------------------------------------------
    // The pin flight (release path only). The mid-drag hysteresis commit
    // never flights: it lands the row through SetPinned + Move and the
    // follow expression carries it through the churn -- the row never
    // detaches, so there is nothing to fly. A release on the preview is
    // different: the churn replaces the dragged element outright, so the
    // settle spring has nothing left to move -- the ghost does instead.
    // It departs from where the eye holds the row (arranged slot plus the
    // follow offset), travels to the slot the preview promised, bounces
    // once, and crossfades into the real prefix-end row. State has
    // already landed before any of it runs; the flight is decoration,
    // and with motion off the release stays the cut it always was.
    // -----------------------------------------------------------------

    /// <summary>
    /// Where the dragged row sits right now, in this control's coordinates:
    /// the arranged slot plus the follow offset the row rides under the
    /// pointer. Arranged truth plus the one offset the drag owns -- never a
    /// neighbour's glide-in-flight position.
    /// </summary>
    private Rect? DraggedRowRect(DragSession drag)
    {
        if (RowElementOf(drag.Tab) is not { } row) return null;
        try
        {
            var pos = row.TransformToVisual(this)
                .TransformPoint(new Point(0, 0));
            if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)
                || row.ActualWidth <= 0 || row.ActualHeight <= 0)
                return null;
            return new Rect(
                pos.X, pos.Y + (drag.LastPointerY - drag.AnchorY),
                row.ActualWidth, row.ActualHeight);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Fly the just-pinned row from <paramref name="start"/> to
    /// <paramref name="dest"/>, the slot the preview showed. Unreadable
    /// endpoints are a cut, the same honesty the preview's own NaN gate
    /// keeps -- an unmeasurable flight is no flight.
    /// </summary>
    private void StartPinFlight(DragSession drag, Rect start, Rect dest)
    {
        // The gate is the drag's own, read at lift: OS animation sources
        // are the wiring's business, never re-derived mid-gesture.
        if (!drag.MotionOn) return;
        // One flight at a time: a live one yields to the fresh release.
        FinishPinFlight("superseded");
        if (RowElementOf(drag.Tab) is not { } row) return;

        var chrome = ActiveRowChrome(drag.Tab);
        var ghost = new Ghostty.Shell.TabMorphGhost(
            drag.Tab, chrome.Fill, chrome.Foreground, new CornerRadius(0))
        {
            Width = dest.Width,
            Height = dest.Height,
        };
        // The destination is an icon-only shelf row, so the ghost travels
        // as one: it arrives in the shape it hands over to.
        ghost.Label.Visibility = Visibility.Collapsed;

        // Placed at the destination and offset back to the start, the gap
        // glides' pattern: the canvas slot stays the truth and the
        // animation only ever travels to zero.
        Canvas.SetLeft(ghost, dest.X);
        Canvas.SetTop(ghost, dest.Y);
        PreviewHost.Children.Add(ghost);
        PreviewHost.Visibility = Visibility.Visible;

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(ghost, true);
            // The row's visual is probed, not kept: composition refusing
            // here is a cut, the same refusal family every layout read
            // in the drag guards.
            _ = ElementCompositionPreview.GetElementVisual(row);
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            PreviewHost.Children.Remove(ghost);
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(ghost);
        var compositor = visual.Compositor;
        var flight = new PinFlight
        {
            Ghost = ghost,
            Visual = visual,
            Row = row,
            Guard = DispatcherQueue.CreateTimer(),
        };
        _pinFlight = flight;

        // State first, then the hide: the field write above is the
        // flight's birth, and anything that throws before it must leave
        // the row untouched -- nothing would exist to restore it. From
        // here the real row waits hidden for the crossfade, and only
        // the flight's completion restores it -- never the reverse.
        row.Opacity = 0;

        // The guard is the completion path's backstop: a batch that never
        // fires (composition wedged, window minimized through the flight)
        // must not leave the real row at opacity zero. One shot, longer
        // than every phase together; landing through it is identical to
        // landing through a batch.
        flight.Guard.IsRepeating = false;
        flight.Guard.Interval = TimeSpan.FromMilliseconds(
            TabStripMotion.PinFlightMs + 3 * TabStripMotion.PinSettlePeriodMs
            + TabStripMotion.FadeMs + 250);
        flight.Guard.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_pinFlight, flight)) return;
            FinishPinFlight("timeout");
        };
        flight.Guard.Start();

        // Phase 1: the flight itself, on the neighbours' curve. The ghost
        // is programmatic -- it starts at velocity 0, never the gesture's.
        var fly = compositor.CreateVector3KeyFrameAnimation();
        fly.Duration = TimeSpan.FromMilliseconds(TabStripMotion.PinFlightMs);
        fly.InsertKeyFrame(1f, Vector3.Zero,
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.55f, 0.55f), new Vector2(0f, 1f)));
        visual.Properties.InsertVector3("Translation", new Vector3(
            (float)(start.X - dest.X), (float)(start.Y - dest.Y), 0f));
        var flying = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        // Identity-guarded, the settle batch's rule: these callbacks fire
        // long after the release, and only the flight still in the field
        // may run its next phase or its landing.
        flying.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pinFlight, flight)) return;
            StartPinSettle(flight);
        };
        visual.StartAnimation("Translation", fly);
        flying.End();
        DragTrace($"DRAG flight start dy={start.Y - dest.Y:0}");
    }

    /// <summary>
    /// Phase 2: the landing's one visible bounce, the bounciest tier in
    /// the strip and the only overshoot the strip spends on purpose. The
    /// ghost arrives slightly lifted and the spring settles it to rest.
    /// </summary>
    private void StartPinSettle(PinFlight flight)
    {
        flight.Visual.Scale = new Vector3(
            TabStripMotion.LiftScale, TabStripMotion.LiftScale, 1f);
        var spring = flight.Visual.Compositor.CreateSpringVector3Animation();
        spring.DampingRatio = TabStripMotion.PinSettleDampingRatio;
        spring.Period = TimeSpan.FromMilliseconds(TabStripMotion.PinSettlePeriodMs);
        spring.FinalValue = new Vector3(1f, 1f, 1f);
        var settling = flight.Visual.Compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);
        settling.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pinFlight, flight)) return;
            StartPinHandback(flight);
        };
        flight.Visual.StartAnimation("Scale", spring);
        settling.End();
    }

    /// <summary>
    /// Phase 3: the handoff. Ghost and real row crossfade on one batch --
    /// one clock, so there is no frame where both are gone.
    /// </summary>
    private void StartPinHandback(PinFlight flight)
    {
        var compositor = flight.Visual.Compositor;
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(TabStripMotion.FadeMs);
        fadeOut.InsertKeyFrame(1f, 0f);
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(TabStripMotion.FadeMs);
        fadeIn.InsertKeyFrame(1f, 1f);
        var handing = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        handing.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pinFlight, flight)) return;
            FinishPinFlight("landed");
        };
        flight.Visual.StartAnimation("Opacity", fadeOut);
        ElementCompositionPreview.GetElementVisual(flight.Row)
            .StartAnimation("Opacity", fadeIn);
        handing.End();
    }

    /// <summary>
    /// Take the flight down and hand the row back at full strength, on
    /// every path that can reach here: landing, a superseding flight, a
    /// new drag, strip teardown, or the guard's timeout. Idempotent by
    /// the single-field identity: whoever runs first clears it.
    /// </summary>
    private void FinishPinFlight(string reason)
    {
        if (_pinFlight is not { } flight) return;
        _pinFlight = null;
        flight.Guard.Stop();
        try
        {
            flight.Visual.StopAnimation("Translation");
            flight.Visual.StopAnimation("Scale");
            flight.Visual.StopAnimation("Opacity");
            // Stopping an animation reverts the property to its set value,
            // so the row's opacity is written, not assumed.
            flight.Row.Opacity = 1;
            ElementCompositionPreview.GetElementVisual(flight.Row)
                .StopAnimation("Opacity");
        }
        catch (Exception ex) when (IsLayoutReadFailure(ex))
        {
            // The row's tree went away mid-flight; the ghost's removal
            // below is the part that still matters.
        }
        PreviewHost.Children.Remove(flight.Ghost);
        if (PreviewHost.Children.Count == 0)
            PreviewHost.Visibility = Visibility.Collapsed;
        DragTrace($"DRAG flight {reason} ghosts={CountLeakedMotion()}");
    }

    /// <summary>
    /// The drag machine's slots: every row with a position on screen, in
    /// manager order, paired with its manager index (SLOT -> MANAGER).
    /// Collapse-as-visibility is what creates the rows this list omits:
    /// a hidden member has no arranged center, and a NaN center would
    /// swallow every crossing past the group, so the machine speaks slots
    /// and its crossing contract holds in their presence. The shape and
    /// its inverse live on the projection, which both strips read.
    /// </summary>
    private (List<TabModel> Rows, List<int> ManagerIndex) DragSlots()
        => TabStripProjection.DragSlots(_manager);

    /// <summary>
    /// MANAGER -> SLOT: the inverse of DragSlots' SLOT -> MANAGER pairing.
    /// </summary>
    private int SlotIndexOf(List<int> managerIndex, TabModel tab)
        => TabStripProjection.SlotIndexOf(_manager, managerIndex, tab);

    private (double Center, double[] Centers) MeasureRows(TabModel dragged)
    {
        var (rows, _) = DragSlots();
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
    /// <summary>
    /// The body slot whose row band contains <paramref name="y"/> -- the
    /// row the release point is over. Above the first row: the first
    /// slot; below the last: the last. -1 when the body has no rendered
    /// rows (the caller keeps the mid-drag state rather than guess).
    /// </summary>
    private int BodySlotAtY(double y)
    {
        var (rows, managerIndex) = DragSlots();
        var best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < rows.Count; i++)
        {
            var center = RowCenterY(rows[i]);
            if (double.IsNaN(center)) continue;
            var d = Math.Abs(center - y);
            if (d < bestDist) { bestDist = d; best = managerIndex[i]; }
        }
        return best;
    }

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
    /// An element's centre X in strip space, for the one caller that needs
    /// a second axis: the seam's drag, which has to be able to name a square
    /// on a band row. NaN when the element has no arranged truth, the same
    /// refusal <see cref="RowCenterY"/> makes.
    /// </summary>
    private double RowCenterX(FrameworkElement item)
    {
        if (item.ActualWidth <= 0) return double.NaN;
        try
        {
            return item.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).X + item.ActualWidth / 2;
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
        HidePinPreview();
        ClearJoinDwell();
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
        // This method is the funnel every exit passes through -- drop,
        // drop with nothing committed, cancel, and strip teardown all land
        // here -- so the end of a live drag has exactly one home. The
        // horizontal run label's machine lifts its drag refusal through
        // the window on this raise; without it the refusal would outlive
        // every vertical drag.
        DragVisualEnded?.Invoke();
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
        // The co-drag rows are the dragged visual's cargo: they come home
        // in the same handback, on every path that reaches here (cut and
        // settled alike -- a settle's batch never fires for a superseded
        // session, and a superseding drag on the same header re-arms the
        // stack itself before this could run).
        DetachCoDrag(drag);
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
            //
            // The dwell is cleared anyway. The invariant this file states --
            // every path that nulls _drag takes the ring back with it -- is
            // worth more than the one branch where it is currently
            // unreachable, and the horizontal strip's CancelHorizontalDrag
            // already reasons exactly this way one method along, about a press
            // that stayed a click while an earlier gesture's ring was still up.
            ClearJoinDwell();
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
    /// not yet handed back, any teardown step that had to be abandoned,
    /// and a pin flight still in the air. The oracle reads anything above
    /// zero as a leak.
    /// </summary>
    private int CountLeakedMotion() =>
        _gapMotion.Count + _gapMotionHeaders.Count + _teardownFailures
        + (_pinFlight is null ? 0 : 1);

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

    // -----------------------------------------------------------------
    // The test seam's drag driver (WINTTY_TEST_SEAM=1). The gesture is
    // the pointer path's own: DragPress/DragMove/DragRelease above, fed
    // positions read out of arranged layout and stepped through a
    // Low-priority dispatcher handoff per tick, so every synthetic move
    // is evaluated -- crossings committed -- before the next one is fed.
    // No OS input, no focus, no second implementation of the grammar.
    // -----------------------------------------------------------------

    /// <summary>True while a gesture (seam-driven or real) holds the strip.</summary>
    internal bool TestSeamDragLive => _drag is not null;

    /// <summary>The pane width the last ApplyPaneLayout named, for the seam.</summary>
    internal double TestSeamPaneWidth => _paneWidth;

    /// <summary>
    /// The strip's arranged geometry, in this control's own coordinate
    /// space. Every chrome question the strip's layout answers -- where the
    /// pinned band ends, how far the close glyph is from the pane edge,
    /// whether a header's content fits -- is a rect comparison, and reading
    /// the rects beats sampling pixels: Mica puts the desktop behind the
    /// strip, so what a screen grab shows depends on the wallpaper.
    ///
    /// Read-only by construction: nothing here measures, arranges, or
    /// mutates, so a driver can ask at any settled moment.
    /// </summary>
    internal void TestSeamWriteElementRects(System.Text.Json.Utf8JsonWriter json)
    {
        json.WriteStartObject("rects");
        WriteSeamRect(json, "strip", this);
        WriteSeamRect(json, "pane", NavView);
        // The band, not a rule between the zones: the pinned zone's edge
        // IS this box's bottom, so a chrome oracle measures the structure
        // rather than a stroke that no longer exists.
        WriteSeamRect(json, "band", _pinnedPanel);

        // Manager order, not dictionary order: a driver indexes these
        // against the state block in the same response.
        json.WriteStartArray("pinned");
        foreach (var tab in _manager.Tabs)
        {
            if (!_pinnedRows.TryGetValue(tab, out var row)) continue;
            json.WriteStartObject();
            json.WriteString("title", tab.EffectiveTitle);
            WriteSeamRect(json, "row", row);
            WriteSeamRect(json, "icon", row.TestSeamIconSlot);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("rows");
        foreach (var tab in _manager.Tabs)
        {
            if (!_items.TryGetValue(tab, out var item)) continue;
            json.WriteStartObject();
            json.WriteString("title", tab.EffectiveTitle);
            WriteSeamRect(json, "row", item);
            WriteSeamRect(json, "content", item.Content as FrameworkElement);
            WriteSeamRect(json, "close",
                (item.Content as VerticalTabNavRow)?.TestSeamCloseButton);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("headers");
        foreach (var group in _manager.Groups)
        {
            if (!_headers.TryGetValue(group, out var item)) continue;
            var row = item.Content as VerticalTabGroupHeaderRow;
            json.WriteStartObject();
            json.WriteString("title", group.Title);
            WriteSeamRect(json, "row", item);
            WriteSeamRect(json, "content", row);
            WriteSeamRect(json, "swatch", row?.TestSeamSwatch);
            WriteSeamRect(json, "chevron", row?.TestSeamChevron);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// One element's arranged box. An element with no box -- collapsed,
    /// unrealized, or mid-teardown -- reports visible=false and no numbers,
    /// which is an answer rather than an error: "the compact pane hides the
    /// close glyph" is exactly what a driver needs to hear.
    /// </summary>
    private void WriteSeamRect(
        System.Text.Json.Utf8JsonWriter json, string name, FrameworkElement? element)
    {
        Point? origin = null;
        if (element is { Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 })
        {
            try
            {
                origin = element.TransformToVisual(this).TransformPoint(new Point(0, 0));
            }
            catch (Exception ex) when (IsLayoutReadFailure(ex))
            {
                origin = null;
            }
        }

        json.WriteStartObject(name);
        json.WriteBoolean("visible", origin is not null);
        if (origin is { } at && element is not null)
        {
            json.WriteNumber("x", at.X);
            json.WriteNumber("y", at.Y);
            json.WriteNumber("w", element.ActualWidth);
            json.WriteNumber("h", element.ActualHeight);
        }
        json.WriteEndObject();
    }

    /// <summary>
    /// What the strip is actually showing for <paramref name="tab"/>: the
    /// body row's own TextBlock, and the icon element the factory built for
    /// the nav item. Both come off the live tree, so a label assertion can
    /// fail even when the model behind it is right. Null when the tab has no
    /// body row (it lives in the pinned prefix, or the strip has not built
    /// its item yet).
    /// </summary>
    internal (string Title, IconElement? Icon)? TestSeamRenderedRow(TabModel tab)
        => _items.TryGetValue(tab, out var item)
           && item.Content is VerticalTabNavRow row
            ? (row.TestSeamRenderedTitle, item.Icon)
            : null;


    /// The rail's inventory, top to bottom: the pinned shelf, then the
    /// list. Reported in tree order rather than projection order because
    /// the projection is the intent and this is the evidence -- a member
    /// the collapse pass has not yet hidden is exactly the row a filmstrip
    /// needs to catch, and the projection would never name it.
    /// </summary>
    internal IReadOnlyList<Testing.TestSeamStripRow> TestSeamRows(FrameworkElement root)
    {
        var rows = new List<Testing.TestSeamStripRow>(
            _pinnedPanel.Children.Count + NavView.MenuItems.Count);

        foreach (var child in _pinnedPanel.Children)
        {
            if (child is not VerticalTabPinnedRow row || row.Tag is not TabModel tab)
                continue;
            rows.Add(Describe(root, row, "pinned", tab));
        }

        foreach (var entry in NavView.MenuItems)
        {
            if (entry is not NavigationViewItem item) continue;
            if (item.Tag is TabGroup group)
            {
                rows.Add(Testing.TestSeamStripRowMeasure.Row(
                    root, item, "header", group.Title, group.Title, active: false));
                continue;
            }
            if (item.Tag is TabModel tab) rows.Add(Describe(root, item, "tab", tab));
        }
        return rows;

        Testing.TestSeamStripRow Describe(
            FrameworkElement r, FrameworkElement el, string kind, TabModel tab)
            => Testing.TestSeamStripRowMeasure.Row(
                r, el, kind, tab.EffectiveTitle, tab.Group?.Title,
                ReferenceEquals(tab, _manager.ActiveTab));
    }

    /// <summary>
    /// One seam drag: press the row of manager index <paramref name="from"/>,
    /// walk the pointer to the slot of manager index <paramref name="to"/>,
    /// release. The outcome carries the manager order after the settle so the
    /// driver can assert in one round trip. A gesture that cannot run reports
    /// ok=false with the reason instead of throwing: the seam reports, it
    /// does not crash the app under test.
    /// </summary>
    internal async Task<Testing.TestSeamDragOutcome> TestSeamDragAsync(int from, int to)
    {
        var outcome = new Testing.TestSeamDragOutcome();
        var tabs = _manager.Tabs;
        if (from < 0 || from >= tabs.Count || to < 0 || to >= tabs.Count)
            return outcome.Fail($"drag {from}->{to} out of range (tabs={tabs.Count})");
        if (from == to)
            return outcome.Fail("drag from == to; nothing to reorder");
        if (_drag is not null)
            return outcome.Fail("another drag is live");

        var tab = tabs[from];
        if (RowElementOf(tab) is not { } row)
            return outcome.Fail("drag row has no element; the strip is not realized");
        double y = RowCenterY(tab);
        if (double.IsNaN(y))
            return outcome.Fail("drag row has no arranged center; layout has not run");

        const uint seamPointer = 0x5749_4E54; // 'WINT'; no real pointer carries it
        // The pointer's X, carried so a walk that ends inside the pinned
        // band can say WHICH square it means. A band row holds several
        // squares at one Y, so a Y-only walk cannot name one of them --
        // which is the whole reason the band answers a hit test rather than
        // a crossing. Seeded from the row's own centre, so a drag that
        // never touches the band moves along the axis it always did.
        double x = RowCenterX(row);
        DragPress(row, seamPointer, y, x);

        // Steps of 12px: past the 4px start threshold on the first move,
        // under the row pitch, so each tick is one honest crossing decision
        // -- the pacing of a slow human finger, at seam speed.
        const double stepPx = 12;
        var reached = false;
        for (int tick = 0; tick < TestSeamDragTickBudget; tick++)
        {
            if (_drag is null) break; // the gesture died under us (layout, close)

            var (rows, managerIndex) = DragSlots();
            int slot = managerIndex.IndexOf(to);
            // A destination inside the band is a POINT, not a height: the
            // band's own slot rect, which is the same geometry the drop
            // preview and the panel's arrange read. Outside it, the row's
            // centre height and the X the walk already holds.
            var band = to < _manager.PinCount ? BandSlotRect(slot) : default;
            var toBand = to < _manager.PinCount && !double.IsNaN(band.X);
            double target = toBand
                ? band.Y + band.Height / 2
                : slot >= 0 && slot < rows.Count ? RowCenterY(rows[slot]) : double.NaN;
            double targetX = toBand ? band.X + band.Width / 2 : x;
            // An unreadable target means the strip is mid-arrange (a commit
            // churned the containers); stand still this tick and re-read
            // the next, exactly as a hovering pointer would.
            if (!double.IsNaN(target))
            {
                // A crossing commits only once the dragged center passes
                // the neighbour's center PLUS the hysteresis token
                // (TabDragReorder.Evaluate's strict inequality), so a walk
                // that aims AT the slot's center stalls one token short of
                // the final commit. Until the manager has moved the row to
                // its slot, the walk aims past the center by the machine's
                // own token; once the commit lands it comes back to the
                // center and settles there -- the overshoot-and-return a
                // human finger performs.
                //
                // The band takes no such token: it commits on the NEAREST
                // square, so aiming at the square's centre is already the
                // answer and an overshoot would aim at its neighbour.
                bool committed = _manager.IndexOf(tab) == to;
                if (committed && Math.Abs(target - y) <= 1 && Math.Abs(targetX - x) <= 1)
                {
                    reached = true;
                    break;
                }
                double aim = committed || toBand
                    ? target
                    : target + Math.Sign(to - from)
                        * (TabStripMotion.CrossingHysteresisPx + 4);
                y += Math.Clamp(aim - y, -stepPx, stepPx);
                x += Math.Clamp(targetX - x, -stepPx, stepPx);
                DragMove(seamPointer, y, x);
            }

            // One handoff below the drag tick's Normal priority: the
            // evaluate this move scheduled has run -- crossings included --
            // by the time control is back.
            await Testing.TestSeam.WaitForLowPriorityAsync(DispatcherQueue);
        }

        if (_drag is not { } drag)
            return outcome.Fail("the gesture ended before the release");
        if (!reached)
        {
            DragCancel(seamPointer);
            return outcome.Fail(
                $"drag did not reach its slot within {TestSeamDragTickBudget} ticks " +
                $"(machine index {drag.Machine.Index}, wanted {to})");
        }

        DragRelease(seamPointer, y);
        outcome.Landed = _manager.IndexOf(tab);
        // A drag into the band CHANGES this, and the manager is the only
        // honest witness to it: the release classifies pin-out, so the
        // gesture's own aim does not tell a caller what it got. Reported
        // by every other drag entry point, and its absence here read as a
        // product bug for as long as this path skipped it.
        outcome.Pinned = tab.IsPinned;
        outcome.Order = TabStripProjection.Rows(_manager)
            .Select(t => t.EffectiveTitle).ToList();
        if (outcome.Landed != to)
            return outcome.Fail($"landed at {outcome.Landed}, wanted {to}");
        return outcome;
    }

    /// <summary>
    /// Generous, because each tick is a dispatcher pass and the strip may
    /// spend several of them mid-arrange after a commit; a healthy drag
    /// lands in a handful. Bounded so a wedged layout reports a failure
    /// instead of hanging the seam.
    /// </summary>
    private const int TestSeamDragTickBudget = 600;

    /// <summary>The synthetic pointer id every seam gesture rides; no real pointer carries it.</summary>
    private const uint TestSeamPointerId = 0x5749_4E54; // 'WINT'

    /// <summary>
    /// The shared walk under a held seam pointer: step toward
    /// <paramref name="target"/> (re-read per tick, NaN = mid-arrange,
    /// stand still) in <paramref name="stepPx"/> steps, one Low-priority
    /// handoff per tick so every move's evaluate -- crossings included --
    /// has run before the next. <paramref name="tickDelayMs"/> &gt; 0 adds
    /// wall-clock pacing per tick, for a capture harness that needs frames
    /// between moves. Returns the settled y, or NaN when the gesture died
    /// or the budget ran out.
    /// </summary>
    private async Task<double> SeamWalkAsync(
        double y, Func<double> target, double stepPx = 12, int tickDelayMs = 0)
    {
        for (int tick = 0; tick < TestSeamDragTickBudget; tick++)
        {
            if (_drag is null) return double.NaN;
            var t = target();
            if (!double.IsNaN(t))
            {
                if (Math.Abs(t - y) <= 1) return y;
                y += Math.Clamp(t - y, -stepPx, stepPx);
                DragMove(TestSeamPointerId, y);
            }
            await Testing.TestSeam.WaitForLowPriorityAsync(DispatcherQueue);
            if (tickDelayMs > 0) await Task.Delay(tickDelayMs);
        }
        return double.NaN;
    }

    /// <summary>A few settled dispatcher passes: the dwell a hovering pointer spends over a slot.</summary>
    private async Task SeamDwellAsync(int ticks)
    {
        for (int i = 0; i < ticks && _drag is not null; i++)
            await Testing.TestSeam.WaitForLowPriorityAsync(DispatcherQueue);
    }

    /// <summary>
    /// The row's arranged center, waited for: a command acked mid-churn
    /// (a collapse's reconcile, a pin's relocation) can leave the row's
    /// replacement element unmeasured for a few dispatcher passes, and a
    /// gesture must not refuse over a rect that is merely late.
    /// </summary>
    private async Task<double> SeamArrangedCenterAsync(TabModel tab)
    {
        for (int i = 0; i < 40; i++)
        {
            var y = RowCenterY(tab);
            if (!double.IsNaN(y)) return y;
            await Testing.TestSeam.WaitForLowPriorityAsync(DispatcherQueue);
        }
        return double.NaN;
    }

    /// <summary>The header row's arranged center, NaN while unarranged; RowCenterY's twin for groups.</summary>
    private double HeaderCenterY(TabGroup group)
    {
        if (!_headers.TryGetValue(group, out var item) || item.ActualHeight <= 0)
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
    /// The paced seam drag: the same gesture as <see cref="TestSeamDragAsync"/>,
    /// walked in fine steps with real wall-clock ticks so a filming harness
    /// has frames to measure the glide against. The outcome timestamps the
    /// commit (first tick the manager index moved) and the release on the
    /// gesture's own clock, so the driver can align its frame times to the
    /// moments the oracle measures from.
    /// </summary>
    internal async Task<Testing.TestSeamDragOutcome> TestSeamDragPacedAsync(
        int from, int to, int tickMs)
    {
        var outcome = new Testing.TestSeamDragOutcome();
        var tabs = _manager.Tabs;
        if (from < 0 || from >= tabs.Count || to < 0 || to >= tabs.Count)
            return outcome.Fail($"drag {from}->{to} out of range (tabs={tabs.Count})");
        if (from == to)
            return outcome.Fail("drag from == to; nothing to reorder");
        if (_drag is not null)
            return outcome.Fail("another drag is live");
        if (tickMs < 1 || tickMs > 500)
            return outcome.Fail("tickMs must be 1..500");

        var tab = tabs[from];
        if (RowElementOf(tab) is not { } row)
            return outcome.Fail("drag row has no element; the strip is not realized");
        double y = RowCenterY(tab);
        if (double.IsNaN(y))
            return outcome.Fail("drag row has no arranged center; layout has not run");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        DragPress(row, TestSeamPointerId, y);

        // 4px steps: a slow finger's pace, so at tickMs per step the walk
        // spans real frames instead of committing inside one. The aim
        // carries the same overshoot discipline as TestSeamDragAsync: past
        // the target center by the machine's own token until the commit
        // lands, then back to the center.
        double Walked()
        {
            if (outcome.CommitMs < 0 && _manager.IndexOf(tab) != from)
                outcome.CommitMs = clock.ElapsedMilliseconds;
            var (rows, managerIndex) = DragSlots();
            int slot = managerIndex.IndexOf(to);
            double target = slot >= 0 && slot < rows.Count
                ? RowCenterY(rows[slot])
                : double.NaN;
            if (double.IsNaN(target) || _manager.IndexOf(tab) == to)
                return target;
            return target + Math.Sign(to - from)
                * (TabStripMotion.CrossingHysteresisPx + 4);
        }
        y = await SeamWalkAsync(y, Walked, stepPx: 4, tickDelayMs: tickMs);
        if (outcome.CommitMs < 0 && _manager.IndexOf(tab) != from)
            outcome.CommitMs = clock.ElapsedMilliseconds;

        if (_drag is not { } drag)
            return outcome.Fail("the gesture ended before the release");
        if (double.IsNaN(y))
        {
            DragCancel(TestSeamPointerId);
            return outcome.Fail(
                $"paced drag did not reach its slot within {TestSeamDragTickBudget} ticks " +
                $"(machine index {drag.Machine.Index}, wanted {to})");
        }

        DragRelease(TestSeamPointerId, y);
        outcome.ReleaseMs = clock.ElapsedMilliseconds;
        outcome.Landed = _manager.IndexOf(tab);
        outcome.Pinned = tab.IsPinned;
        outcome.Order = TabStripProjection.Rows(_manager)
            .Select(t => t.EffectiveTitle).ToList();
        if (outcome.Landed != to)
            return outcome.Fail($"landed at {outcome.Landed}, wanted {to}");
        return outcome;
    }

    /// <summary>
    /// The pin-boundary gesture, both halves of the release-classified
    /// contract: press a body row, carry it up past the pinned zone's last
    /// row by the machine's own crossing hysteresis plus margin (the
    /// crossing pins it mid-gesture), dwell with the preview live, then
    /// either release inside the shelf (the pin holds) or carry it a full
    /// row back below the boundary and release (the release unpins). The
    /// zone target is read from the same constants the machine evaluates
    /// with, never re-derived, so a token change moves this gesture too.
    /// </summary>
    internal async Task<Testing.TestSeamDragOutcome> TestSeamDragZoneAsync(
        int from, bool releaseInZone)
    {
        var outcome = new Testing.TestSeamDragOutcome();
        var tabs = _manager.Tabs;
        if (from < 0 || from >= tabs.Count)
            return outcome.Fail($"drag-zone from {from} out of range (tabs={tabs.Count})");
        var tab = tabs[from];
        if (tab.IsPinned)
            return outcome.Fail("drag-zone needs an unpinned row");
        if (_manager.PinCount == 0)
            return outcome.Fail("no pinned zone to cross into");
        if (_drag is not null)
            return outcome.Fail("another drag is live");
        if (RowElementOf(tab) is not { } row)
            return outcome.Fail("drag row has no element; the strip is not realized");
        double homeY = await SeamArrangedCenterAsync(tab);
        var zoneY = await SeamArrangedCenterAsync(tabs[_manager.PinCount - 1]);
        if (double.IsNaN(homeY) || double.IsNaN(zoneY))
            return outcome.Fail("layout has not arranged the boundary rows");
        double rowPitch = row.ActualHeight > 0 ? row.ActualHeight : VerticalTabPinnedRow.RowHeight;
        double overshootY = zoneY - (TabStripMotion.CrossingHysteresisPx + 12);

        DragPress(row, TestSeamPointerId, homeY);
        var y = await SeamWalkAsync(homeY, () => overshootY);
        if (double.IsNaN(y))
        {
            DragCancel(TestSeamPointerId);
            return outcome.Fail("the zone crossing never completed");
        }
        // In the zone long enough for the pin preview to be live before
        // the pointer settles on where it lets go.
        await SeamDwellAsync(3);

        y = await SeamWalkAsync(y, () => releaseInZone ? zoneY : homeY + rowPitch);
        if (_drag is null || double.IsNaN(y))
        {
            DragCancel(TestSeamPointerId);
            return outcome.Fail("the return walk never completed");
        }
        // Let the arrange catch up before letting go: the release
        // classifies by the body slot under the pointer, and a release
        // into a mid-flight arrange resolves the slot boundary by timing
        // instead of geometry.
        await SeamDwellAsync(3);
        DragRelease(TestSeamPointerId, y);
        outcome.Landed = _manager.IndexOf(tab);
        outcome.Pinned = tab.IsPinned;
        outcome.Order = TabStripProjection.Rows(_manager)
            .Select(t => t.EffectiveTitle).ToList();
        return outcome;
    }

    /// <summary>
    /// The drop-on-header gesture: press a body row, walk it onto the
    /// named group's header row (re-read per tick; the header moves as
    /// crossings churn the list), dwell, and release there. What the
    /// release does with that landing -- the join, the auto-expand -- is
    /// the product's own drop grammar; the outcome only reports where the
    /// row ended, and the driver asserts membership through get-state.
    /// </summary>
    internal async Task<Testing.TestSeamDragOutcome> TestSeamDragToHeaderAsync(
        int from, string groupTitle)
    {
        var outcome = new Testing.TestSeamDragOutcome();
        var tabs = _manager.Tabs;
        if (from < 0 || from >= tabs.Count)
            return outcome.Fail($"drag-header from {from} out of range (tabs={tabs.Count})");
        var tab = tabs[from];
        TabGroup? group = null;
        foreach (var candidate in _manager.Groups)
            if (candidate.Title == groupTitle) { group = candidate; break; }
        if (group is null)
            return outcome.Fail($"no group titled '{groupTitle}'");
        if (_drag is not null)
            return outcome.Fail("another drag is live");
        if (RowElementOf(tab) is not { } row)
            return outcome.Fail("drag row has no element; the strip is not realized");
        double y = await SeamArrangedCenterAsync(tab);
        if (double.IsNaN(y))
            return outcome.Fail("layout has not arranged the dragged row");

        // No arranged-header precheck: a collapse ack can land while the
        // header's replacement element is still unmeasured, and the walk
        // already stands still on a NaN target until the arrange catches
        // up (budget-bounded).
        DragPress(row, TestSeamPointerId, y);
        y = await SeamWalkAsync(y, () => HeaderCenterY(group));
        if (_drag is null || double.IsNaN(y))
        {
            DragCancel(TestSeamPointerId);
            return outcome.Fail("the walk to the header never completed");
        }
        // The hold over the header the pointer gesture spends before it
        // lets go.
        await SeamDwellAsync(4);
        DragRelease(TestSeamPointerId, y);
        outcome.Landed = _manager.IndexOf(tab);
        outcome.Pinned = tab.IsPinned;
        outcome.Order = TabStripProjection.Rows(_manager)
            .Select(t => t.EffectiveTitle).ToList();
        return outcome;
    }

    /// <summary>
    /// The join gesture, both outcomes of the hold-with-a-ring contract:
    /// press a row, walk it onto its NEIGHBOUR and stop there, then
    /// either hold until the ring completes (the release joins the two
    /// into a group) or let go at once (the release is the ordinary sort,
    /// and nothing is grouped).
    ///
    /// The walk stops on the neighbour's arranged center rather than past
    /// it, and that is what makes it a join gesture instead of a reorder:
    /// a crossing wants that center PLUS the machine's own hysteresis
    /// token, so a row resting exactly on it has earned no crossing and
    /// is sitting squarely over the row the ring is a promise about.
    /// Neighbours only, because that is the only thing the ring ever
    /// targets.
    ///
    /// The dwell's clock is pinned for the length of the gesture and the
    /// hold is one assignment to it. Sleeping 450ms here instead would
    /// measure the thread pool: this repo has paid for three tests that
    /// timed a gesture against a wall clock, and the ring is the thing
    /// under test, not the scheduler.
    /// </summary>
    internal async Task<Testing.TestSeamDragOutcome> TestSeamDragJoinAsync(
        int from, int to, bool hold)
    {
        var outcome = new Testing.TestSeamDragOutcome();
        var tabs = _manager.Tabs;
        if (from < 0 || from >= tabs.Count || to < 0 || to >= tabs.Count)
            return outcome.Fail($"drag-join {from}->{to} out of range (tabs={tabs.Count})");
        if (from == to)
            return outcome.Fail("drag-join from == to; a row cannot join itself");
        if (_drag is not null)
            return outcome.Fail("another drag is live");

        var tab = tabs[from];
        var target = tabs[to];
        if (!TabJoinDrop.CanJoin(_manager, tab, target))
            return outcome.Fail(
                $"drag-join {from}->{to} is not a joinable pair (pinned, or already one group)");

        // Adjacency is judged in SLOT space, not manager space: a
        // collapsed run's hidden members hold manager indices and no
        // slots, so two rows that look adjacent in the strip can be
        // several manager indices apart and the other way round.
        var (rows, _) = DragSlots();
        int fromSlot = rows.IndexOf(tab);
        int toSlot = rows.IndexOf(target);
        if (fromSlot < 0 || toSlot < 0)
            return outcome.Fail("drag-join needs both rows visible in the strip");
        if (Math.Abs(fromSlot - toSlot) != 1)
            return outcome.Fail(
                "drag-join needs adjacent rows: the ring only ever targets a neighbour");
        if (RowElementOf(tab) is not { } row)
            return outcome.Fail("drag row has no element; the strip is not realized");

        double y = await SeamArrangedCenterAsync(tab);
        if (double.IsNaN(y))
            return outcome.Fail("layout has not arranged the dragged row");

        _seamJoinClockMs = 0;
        try
        {
            DragPress(row, TestSeamPointerId, y);
            y = await SeamWalkAsync(y, () => RowCenterY(target));
            if (_drag is null || double.IsNaN(y))
            {
                DragCancel(TestSeamPointerId);
                return outcome.Fail("the walk onto the neighbour never completed");
            }
            // The last move's evaluate is what picks the target and
            // anchors the dwell; the ring cannot be advanced before it has
            // run, and the pointer must be still when it does -- travel
            // restarts the dwell.
            await SeamDwellAsync(2);
            if (hold)
            {
                _seamJoinClockMs = (long)TabStripMotion.JoinDwellMs + 1;
                TickJoinDwell();
                await Testing.TestSeam.WaitForLowPriorityAsync(DispatcherQueue);
            }
            var armed = _joinDwell.IsArmed;
            outcome.Armed = armed;
            if (hold && !armed)
            {
                DragCancel(TestSeamPointerId);
                return outcome.Fail("the ring never completed over the neighbour");
            }
            if (_drag is null)
                return outcome.Fail("the gesture ended before the release");
            DragRelease(TestSeamPointerId, y);
        }
        finally
        {
            // Restored on every path, refusals included: a virtual clock
            // left behind would freeze the ring for every later gesture
            // in this process, and the dwell would arm on the first frame
            // or never.
            _seamJoinClockMs = null;
        }
        outcome.Landed = _manager.IndexOf(tab);
        outcome.Pinned = tab.IsPinned;
        outcome.Group = tab.Group?.Title;
        outcome.Order = TabStripProjection.Rows(_manager)
            .Select(t => t.EffectiveTitle).ToList();
        return outcome;
    }
}
