using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Input;
using Ghostty.Panes;
using Ghostty.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Ghostty.Core.Windows;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Ghostty.Tabs;

/// <summary>
/// WinUI host that visualises a <see cref="TabManager"/> as a
/// <see cref="TabView"/>. Owns the bidirectional mapping between
/// <see cref="TabModel"/>s and <see cref="TabViewItem"/>s. The
/// per-tab progress indicator is rendered here as a 2px ProgressBar
/// in the tab header template.
///
/// The vertical layout is a sibling user control, VerticalTabHost,
/// sharing this same TabManager; LayoutCoordinator cross-fades between
/// the two.
/// </summary>
internal sealed partial class TabHost : UserControl, ITabHost
{
    private readonly TabManager _manager;
    private readonly PaneActionRouter _router;
    private readonly DialogTracker _dialogs;
    private readonly Dictionary<TabModel, TabViewItem> _itemByModel = new();
    // Header title TextBlock per tab. Kept so ApplyShellTheme can update
    // the Foreground directly without replacing the StackPanel header
    // (which would drop the 2px progress bar and the tab-color tint).
    private readonly Dictionary<TabModel, TextBlock> _headerTextByModel = new();

    // The horizontal icon row per tab -- pin glyph, profile icon, title,
    // bell glyph. Held by identity because the row is not the header's
    // first child and never was guaranteed to be: RecolorTabText used to
    // reach it as headerPanel.Children[0], which the group rail silently
    // took over (#833), leaving every colour-tagged tab's pin, bell and
    // icon on the untagged ink (#883).
    private readonly Dictionary<TabModel, StackPanel> _iconRowByModel = new();

    // Segoe Fluent / MDL2 "Ringer". Shared with the ink pass, which has to
    // tell the bell from the pin to know which one goes back to the accent.
    private const string BellGlyph = "\uEA8F";

    // Segoe Fluent / MDL2 "QuietHours" moon, shown while TabModel.IsIdle.
    // Shared with the ink pass for the same reason as the bell.
    private const string IdleGlyph = "\uE708";

    // How far an idle tab's icon and title fade. Quiet enough to read as
    // "resting", strong enough to notice in a scan of the strip.
    private const double IdleOpacity = 0.45;

    // Segoe Fluent / MDL2 "Home": drawn in the title's place for a tab
    // sitting in the user's own directory.
    private const string HomeGlyph = "\uE80F";

    // One chip per group the projection renders as collapsed-without-the-
    // active-tab. A chip is a real TabViewItem occupying a strip slot, so
    // every slot count and every slot index now includes chips; the maps
    // above alone no longer describe what TabItems holds.
    private readonly Dictionary<TabGroup, ChipVisuals> _chipByGroup = new();

    // Groups this strip is listening to. Deliberately not keyed off
    // _chipByGroup: see ReconcileGroupHooks for why the two must not
    // share a lifetime.
    private readonly HashSet<TabGroup> _hookedGroups = new();

    // The 2px group rail per tab, in the header's top slot. Kept like the
    // header TextBlock so a group change repaints the live element instead
    // of rebuilding the header.
    private readonly Dictionary<TabModel, Microsoft.UI.Xaml.Shapes.Rectangle>
        _railByModel = new();

    // The group run label. The rule machine decides; ApplyLabelPhase
    // translates each phase into timer arms and element ops. The element
    // lives on the window's morph canvas -- MainWindow attaches it here --
    // because the hide rules come from both strips and the window, and the
    // morph canvas is the one coordinate space all three already share.
    private readonly TabRunLabelRules _labelRules = new();
    private TabRunLabel? _runLabel;
    private TabRunLabelRules.Phase _labelPhase = TabRunLabelRules.Phase.Idle;
    private TabGroup? _labelGroup;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _labelShowTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _labelGraceTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _labelKeyboardTimer;

    /// <summary>
    /// The renderable parts of one chip, kept so INPC updates land on the
    /// live elements instead of rebuilding the header -- the same reason
    /// a header TextBlock is kept per tab.
    /// </summary>
    private sealed record ChipVisuals(
        TabViewItem Item, TextBlock Title, TextBlock Count, Border Swatch, FontIcon Chevron);

    private bool _suppressSelectionEvent;
    // From the process-wide factory rather than injected: this control is
    // built before DI contexts exist and logs one terminal condition only.
    private readonly ILogger _log =
        (ILogger?)App.LoggerFactory?.CreateLogger<TabHost>() ?? NullLogger.Instance;

    public FrameworkElement HostElement => this;

    public FrameworkElement? TabElement(TabModel tab)
        => _itemByModel.TryGetValue(tab, out var item) ? item : null;

    // ---- test seam accessors (WINTTY_TEST_SEAM=1) --------------------
    // Named TestSeam* like MainWindow's, so the seam's footprint on this
    // class is greppable and removable as one shape.

    /// <summary>
    /// One part of a tab's header row, in this control's DIP coordinates:
    /// what a pixel harness needs to sample the ink of a glyph too small
    /// and too unnamed for UIA to hand it a rect. Null when the tab has no
    /// row, the part is not in it, or layout has not given it bounds --
    /// a rect nobody can vouch for is worse than no rect.
    ///
    /// Parts are found by kind, never by index: the same rule the ink pass
    /// itself now follows.
    /// </summary>
    internal Windows.Foundation.Rect? TestSeamHeaderPartRect(TabModel tab, string part)
    {
        if (!_iconRowByModel.TryGetValue(tab, out var row)) return null;
        FrameworkElement? target = part == "row" ? row : null;
        if (target is null)
        {
            foreach (var child in row.Children)
            {
                // No "pin" part: the pushpin is gone, and the pinned tab's
                // mark is its own shape -- which a driver reads as the
                // "row" part's width, not as a glyph inside it.
                //
                // Every part must also be VISIBLE. ActualWidth keeps its
                // last measured value when an element collapses, so a
                // glyph-visibility gate is what makes "no rect" mean
                // "not on screen" rather than "hidden but still holding
                // stale geometry" -- the rect a pixel harness would
                // sample for ink that is not there.
                var match = part switch
                {
                    "bell" => child is FontIcon bell
                        && bell.Glyph == BellGlyph
                        && bell.Visibility == Visibility.Visible,
                    "idle" => child is FontIcon moon
                        && moon.Glyph == IdleGlyph
                        && moon.Visibility == Visibility.Visible,
                    "icon" => child is TabIconPresenter icon
                        && icon.Visibility == Visibility.Visible,
                    "title" => child is TextBlock title
                        && title.Visibility == Visibility.Visible,
                    _ => false,
                };
                if (match) { target = child as FrameworkElement; break; }
            }
        }
        if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            return null;
        try
        {
            var origin = target.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Windows.Foundation.Rect(
                origin.X, origin.Y, target.ActualWidth, target.ActualHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the horizontal strip is actually showing for <paramref name="tab"/>:
    /// its header TextBlock, the TabViewItem's own tooltip, and whether the
    /// home glyph stands in the title's place -- all off the live tree, so a
    /// label assertion can fail even when the model behind it is right. The
    /// title is empty when something else is drawn in its slot, which is what
    /// a person sees. Null when the strip has not built its item yet.
    /// </summary>
    internal (string Title, string? Tooltip, bool HomeGlyph)? TestSeamRenderedItem(TabModel tab)
        => _itemByModel.TryGetValue(tab, out var item) && _headerTextByModel.TryGetValue(tab, out var header)
            ? (header.Visibility == Visibility.Visible ? header.Text : "",
               ToolTipService.GetToolTip(item) as string,
               _iconRowByModel.TryGetValue(tab, out var row)
                   && row.Children.OfType<FontIcon>().Any(f => f.Glyph == HomeGlyph && f.Visibility == Visibility.Visible))
            : null;

    /// <summary>
    /// The foreground the ink pass hands a colour-tagged tab's header row,
    /// as 0x00RRGGBB, or null when the tab carries no tag. The expectation
    /// a pixel harness measures against -- and, on its own, worth nothing:
    /// a brush is only a claim until something paints with it.
    /// </summary>
    internal uint? TestSeamTagForegroundRgb(TabModel tab)
        => tab.Color == TabColor.None
            ? null
            : TabColorPalette.ForegroundRgb(
                tab.Color, ReferenceEquals(tab, _manager.ActiveTab), _stripBackdropPacked);

    /// <summary>
    /// The wintty icon shown at the start of the tab strip. Exposed so
    /// the layout switch can spin it independently of the strip chrome.
    /// </summary>
    public FrameworkElement IconBadge => IconBadgeHost;

    /// <summary>
    /// The Grid that sits in the TabView's TabStripFooter and
    /// reserves room for the OS caption buttons. <see cref="MainWindow"/>
    /// passes this to <c>Window.SetTitleBar</c> so clicks on the
    /// empty strip area drag the window.
    /// </summary>
    public UIElement DragRegion => CustomDragRegion;

    public TabHost(TabManager manager, PaneActionRouter router, DialogTracker dialogs)
    {
        InitializeComponent();
        _manager = manager;
        _router = router;
        _dialogs = dialogs;

        foreach (var t in _manager.Tabs) AddItem(t);
        SelectActive();

        // ReconcileChips rides every manager event because chip presence
        // is a projection function all four can move: a restore can
        // arrive grouped, a close can retire a run's last member, a move
        // can join a chip'd run, and activation is what decides which
        // collapsed run shows its member versus its chip. The events
        // raise whether or not this host is the visible layout, so the
        // strip stays in step while hidden; RefreshSeam re-derives the
        // whole pass as belt and braces.
        _manager.TabAdded += (_, t) => { AddItem(t); ReconcileChips(); ReconcileStripOrder(); SelectActive(); QueueBridgeUpdate(); };
        _manager.TabRemoved += (_, t) => { RemoveItem(t); ReconcileChips(); ReconcileStripOrder(); ApplyPinZoneChrome(); QueueBridgeUpdate(); };
        _manager.TabMoved += (_, e) => { MoveItem(e.tab, e.to); ReconcileChips(); ReconcileStripOrder(); ApplyPinZoneChrome(); QueueBridgeUpdate(); };
        _manager.ActiveTabChanged += (_, _) => { ReconcileChips(); ReconcileStripOrder(); SelectActive(); QueueBridgeUpdate(); };

        // Every one of the calls above can run before the strip has arranged,
        // and the bridge is placed from the selected item's layout slot, so
        // it needs a pass once bounds exist. Width changes move every tab
        // under Equal sizing, so the strip resizing moves it too.
        // Symmetric with the Unloaded unhook below. WinUI raises the pair
        // on reparenting as well as teardown, and a strip that came back
        // deaf to its groups is the #871 shape again by a second door.
        Loaded += (_, _) =>
        {
            ReconcileGroupHooks();
            QueueBridgeUpdate();
        };
        TabViewControl.SizeChanged += (_, _) => UpdateSelectedTabBridge();

        // A lift cannot outlive the strip: teardown finishes it, the same
        // door the vertical's drag and its pin flight go out through. The
        // group hooks go out the same door, for the same reason.
        Unloaded += (_, _) =>
        {
            FinishLift("teardown");
            UnhookAllGroups();
            if (_tabStripScroller is { } scroller)
            {
                scroller.ViewChanged -= OnTabStripScrollerViewChanged;
                _tabStripScroller = null;
            }
        };

        // The drag lifecycle gates the seam cover below: mid-drag the
        // strip's slots are the engine's crossing preview, not the
        // manager's settled order, so the cover (placed from the active
        // item's slot) is suppressed for the drag and re-placed at the
        // drop.
        HookStripDragInput();

        // The run label's timers. Each stops itself before translating, so
        // a timer that fires twice in a pass cannot double-apply.
        _labelShowTimer = DispatcherQueue.CreateTimer();
        _labelShowTimer.Interval = TimeSpan.FromMilliseconds(TabRunLabelShape.HoverShowMs);
        _labelShowTimer.Tick += (_, _) => { _labelShowTimer.Stop(); ApplyLabelPhase(_labelRules.HoverTimerFired()); };
        _labelGraceTimer = DispatcherQueue.CreateTimer();
        _labelGraceTimer.Interval = TimeSpan.FromMilliseconds(TabRunLabelShape.LeaveGraceMs);
        _labelGraceTimer.Tick += (_, _) => { _labelGraceTimer.Stop(); ApplyLabelPhase(_labelRules.GraceTimerFired()); };
        _labelKeyboardTimer = DispatcherQueue.CreateTimer();
        _labelKeyboardTimer.Interval = TimeSpan.FromMilliseconds(TabRunLabelShape.KeyboardShowMs);
        _labelKeyboardTimer.Tick += (_, _) => { _labelKeyboardTimer.Stop(); ApplyLabelPhase(_labelRules.KeyboardTimerFired()); };

        // TabView.TabItemsChanged stays unwired on purpose. It fires for
        // this control's own writes as much as for TabView's, so a
        // validation handler on it would re-enter the reconcile that
        // mutates TabItems. The validation points are the reconciles in
        // MoveItem and the engine's finish pass; both read the manager.
    }

    private void AddItem(TabModel tab)
    {
        // PaneHost parenting and visibility are owned by MainWindow
        // via a shared container, so both tab hosts can coexist without
        // double-parenting the same PaneHost.
        //
        // The TabView item is a header-only placeholder. Content is
        // null on purpose; the actual terminal lives in
        // _paneHostContainer above.
        //
        // Header is a StackPanel with a TextBlock for the title and a
        // 2px ProgressBar stacked below. Both update from TabModel's
        // INPC notifications — TabModel raises EffectiveTitle on title
        // changes and Progress on OSC 9;4 state changes.
        // Trimming is not decoration: the strip shares its width out, so a
        // header that cannot fit is exactly the case where a hover is owed
        // the whole title -- and IsTextTrimmed only ever reports true when a
        // trimming mode is set.
        var headerText = new TextBlock
        {
            Text = tab.EffectiveTitle,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // If the shell theme is already active, paint the new tab's
        // title in the cached active-text brush so tabs opened after
        // ApplyShellTheme match the ones that were present at the time.
        // A tab opened while a shell theme is active starts inactive
        // (the new tab becomes active via TabAdded→SelectActive, which
        // then promotes it). Default to the inactive brush so it never
        // flashes the active-on-inactive-bg invisible state (#342). The
        // active/inactive brushes are a coupled pair (both set in
        // ApplyShellTheme, both nulled in ClearShellTheme), so a non-null
        // inactive brush is sufficient to know a shell theme is active.
        if (_shellInactiveTextBrush is not null)
            headerText.Foreground = _shellInactiveTextBrush;
        var headerBar = new ProgressBar
        {
            Height = 2,
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed,
            IsIndeterminate = false,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var headerPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };

        // Profile icon slot. Built imperatively via TabIconPresenter to
        // sidestep WinUI 3 / CsWinRT 2.x runtime binding, which requires
        // [WinRT.GeneratedBindableCustomProperty] on the bound type and
        // would drag a UI dependency into Ghostty.Core. The presenter
        // subscribes to the TabIconViewModel's INPC events directly.
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };

        // No pin mark. The glyph existed because equal-width kept a
        // pinned tab full-size, so nothing about the slot said "pinned"
        // and an inline marker had to. The slot says it now -- a pinned
        // tab IS an icon square -- and a pushpin crowded into 48px beside
        // the icon it is meant to caption reads as clutter, not as a mark.
        var iconHost = new TabIconPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        iconHost.Attach(tab.TabIcon);
        iconRow.Children.Add(iconHost);
        iconRow.Children.Add(headerText);
        // The home glyph, shown instead of the title when the tab sits in
        // the user's own directory; the anatomy pass toggles the pair.
        var homeGlyph = new FontIcon
        {
            Glyph = HomeGlyph,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = tab.IsHome ? Visibility.Visible : Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var homeFont)
            && homeFont is Microsoft.UI.Xaml.Media.FontFamily homeFamily)
        {
            homeGlyph.FontFamily = homeFamily;
        }
        // The item is already named "Home"; a named child would say it twice.
        AutomationProperties.SetAccessibilityView(
            homeGlyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        iconRow.Children.Add(homeGlyph);

        // Bell indicator: a Ringer glyph shown after the title while the
        // tab has an unacknowledged bell (bell-features `title`). Collapsed
        // by default; toggled from TabModel.BellRinging below.
        var bellGlyph = new FontIcon
        {
            Glyph = BellGlyph,
            // FontIcon's default FontFamily is not guaranteed to be the symbol
            // font, so pin it explicitly (as the strip's close button does) or
            // the glyph can render as nothing.
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)
                Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // Tint with the system accent, matching the vertical strip badge
            // and the macOS/GTK accent-colored bell.
            Foreground = BellAccentBrush(),
            Visibility = tab.BellRinging ? Visibility.Visible : Visibility.Collapsed,
        };
        iconRow.Children.Add(bellGlyph);

        // Idle indicator: the moon after the bell slot while the tab has
        // been untouched (TabIdleTracker). Muted, not accent -- idle is a
        // rest state, not an alert. The bell owns the badge: a ringing tab
        // is never idle, and the property handler below keeps the moon
        // hidden while a bell is up even if IsIdle has not caught up.
        var idleGlyph = new FontIcon
        {
            Glyph = IdleGlyph,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)
                Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = IdleGlyphBrush(),
            Visibility = IdleBadgeVisible(tab) ? Visibility.Visible : Visibility.Collapsed,
        };
        iconRow.Children.Add(idleGlyph);
        // The dim rides the row's own elements rather than the whole
        // TabViewItem: the close button and the hover chrome stay
        // full-strength so an idle tab still reads as fully operable.
        ApplyIdleInk(iconHost, headerText, tab);

        // The group rail: a 2px line in the group's color in the header's
        // TOP slot. The progress bar owns the bottom slot; the two
        // never fight. Collapsed until a chrome pass finds a group to
        // paint -- membership, not this build loop, decides its color.
        var rail = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        headerPanel.Children.Add(rail);
        headerPanel.Children.Add(iconRow);
        headerPanel.Children.Add(headerBar);
        _railByModel[tab] = rail;

        var item = new TabViewItem
        {
            Header = headerPanel,
            Content = null,
            ContextFlyout = TabContextMenuBuilder.Build(
                _manager,
                tab,
                RequestCloseTabAsync,
                requestDetachToNewWindow: RequestDetachToNewWindow,
                _dialogs,
                toggleTabLayout: () => _router.RequestToggleTabLayout(),
                requestPin: _router.RequestPin,
                requestDuplicate: _router.RequestDuplicateTab,
                requestNewGroupWithTab: _router.RequestNewGroupWithTab,
                requestAddToGroup: _router.RequestAddToGroup,
                requestRemoveFromGroup: _router.RequestRemoveFromGroup,
                getSnapSource: GetSnapSource,
                detachWithZone: DetachWithZone),
            DataContext = tab,
        };
        ApplyItemAccessibleText(item, tab);
        // Hover anywhere on the run shows the label. The handlers
        // ride every member item and guard at entry: a tab that is not in
        // an expanded run is not a run surface. Crossing between members
        // of one run fires Exited then Entered, which is exactly what the
        // 150ms grace exists to absorb.
        item.PointerEntered += (_, _) => OnRunMemberPointerEntered(tab);
        item.PointerExited += (_, _) => ApplyLabelPhase(_labelRules.HoverExit());
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabModel.EffectiveTitle) ||
                e.PropertyName == nameof(TabModel.ShellReportedTitle) ||
                e.PropertyName == nameof(TabModel.UserOverrideTitle))
            {
                headerText.Text = tab.EffectiveTitle;
                ApplyItemAccessibleText(item, tab);
                // headerText is COLLAPSED on a pinned tab, so the line above
                // updates something nobody can see and the tooltip -- the
                // square's only visible carrier of the title -- was left
                // saying whatever the tab was called when it was pinned,
                // until a theme change or a drag happened to re-run the
                // anatomy sweep. The vertical band's square already refreshes
                // on these same three property names; this is the horizontal
                // edition catching up.
                ApplyPinnedTabAnatomy(item, tab);
            }
            else if (e.PropertyName == nameof(TabModel.Progress))
            {
                var p = tab.Progress;
                headerBar.Visibility = p.State == TabProgressState.Kind.None
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                headerBar.IsIndeterminate = p.State == TabProgressState.Kind.Indeterminate;
                if (p.State != TabProgressState.Kind.Indeterminate)
                    headerBar.Value = p.Percent;
            }
            else if (e.PropertyName == nameof(TabModel.Color))
            {
                RefreshTabColors();
            }
            else if (e.PropertyName == nameof(TabModel.BellRinging))
            {
                bellGlyph.Visibility = tab.BellRinging
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                // The bell and the moon share the badge slot; whichever
                // way the bell moves, the moon re-derives behind it.
                idleGlyph.Visibility = IdleBadgeVisible(tab)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ApplyItemAccessibleText(item, tab);
            }
            else if (e.PropertyName == nameof(TabModel.IsIdle))
            {
                idleGlyph.Visibility = IdleBadgeVisible(tab)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ApplyIdleInk(iconHost, headerText, tab);
                homeGlyph.Opacity = headerText.Opacity;
            }

            else if (e.PropertyName == nameof(TabModel.IsSettling))
            {
                // The icon carries the start on its own -- the VM swaps it,
                // and the presenter is already listening. What a listener
                // gets is the status text, and only this arm writes it.
                ApplyItemAccessibleText(item, tab);
            }
            else if (e.PropertyName == nameof(TabModel.IsPinned))
            {
                // "Pinned" rides ItemStatus, and the flag change is the
                // only event that always carries it: SetPinned relocates
                // to the zone boundary and skips TabMoved when from == to
                // (the boundary tab pinning up, the last pinned tab
                // unpinning), so a relocation-path refresh alone would
                // leave the boundary tab's status stale for life. The
                // shape follows the flag for exactly the same reason.
                ApplyPinnedTabAnatomy(item, tab);
                ApplyItemAccessibleText(item, tab);
            }
            else if (e.PropertyName == nameof(TabModel.Group))
            {
                // Membership moved and the run shapes changed with it:
                // chip presence, chip counts, and the order they sit in
                // all re-read. A join into a chip'd run also strands this
                // tab's slot; the reconcile's rebuild is what removes it.
                // The rail re-derives on the next chrome pass, which the
                // reconcile's SelectActive neighbors already run -- but a
                // move that lands neither selection nor chip still needs
                // the line to follow the tab, so paint it here too.
                ReconcileChips();
                ReconcileStripOrder();
                ApplyTabChrome(item, headerPanel, tab,
                    ReferenceEquals(tab, _manager.ActiveTab));
            }
        };
        _itemByModel[tab] = item;
        _headerTextByModel[tab] = headerText;
        // The width the strip shares out changes with the tab count and with
        // the window, so whether a label is trimmed changes without the tab
        // changing at all. Only the tooltip re-derives from it.
        headerText.IsTextTrimmedChanged += (_, _) => ApplyPinnedTabAnatomy(item, tab);
        _iconRowByModel[tab] = iconRow;
        TabViewControl.TabItems.Add(item);
        // After the registries: the anatomy pass reads the icon row back
        // out of _iconRowByModel, so a tab that arrives already pinned
        // (a session restore, a detach into a new window) has to be
        // findable before it is asked to take its shape.
        ApplyPinnedTabAnatomy(item, tab);
        ApplyTabChrome(item, headerPanel, tab, selected: false);
    }

    /// <summary>
    /// Screen-reader text for a tab. The header is a StackPanel, and an
    /// unnamed TabViewItem gets no name out of a panel header, so
    /// without this every tab in the strip is nameless -- an automation
    /// client sees how many tabs are open and nothing about any of them.
    /// </summary>
    private static void ApplyItemAccessibleText(TabViewItem item, TabModel tab)
    {
        AutomationProperties.SetName(item, TabAccessibleText.Name(tab));
        AutomationProperties.SetItemStatus(item, TabAccessibleText.Status(tab));
    }

    private void RemoveItem(TabModel tab)
    {
        if (!_itemByModel.TryGetValue(tab, out var item)) return;
        TabViewControl.TabItems.Remove(item);
        _itemByModel.Remove(tab);
        _headerTextByModel.Remove(tab);
        _iconRowByModel.Remove(tab);
        _railByModel.Remove(tab);
        // PaneHost detach from the shared container is MainWindow's job.
    }

    // -----------------------------------------------------------------
    // The group run label. The rules live in
    // TabRunLabelRules (Core, host-free); this half only translates each
    // phase into timer arms and element ops. Every event lands in
    // ApplyLabelPhase, so a hide rule cannot be forgotten by arriving
    // through a door the strip did not expect.
    // -----------------------------------------------------------------

    /// <summary>
    /// Attach the window-owned label element. MainWindow builds and hosts
    /// it on the morph canvas -- the surface both strips are measured in
    /// and the window already owns -- and hands it over here.
    /// </summary>
    internal void AttachRunLabel(TabRunLabel label) => _runLabel = label;

    /// <summary>
    /// The cross-host hide: the vertical strip's drag start reaches this
    /// through the window, in the same dispatch pass as its own lift. The
    /// label cannot know about the vertical strip and must not -- the
    /// window owns the fact that both strips share one drag surface.
    /// </summary>
    internal void CloseRunLabelForDrag() => ApplyLabelPhase(_labelRules.DragStarting());

    /// <summary>
    /// The cross-host lift: the vertical drag ended, so the refusal stops.
    /// Without this the machine's DragLive flag has no off-switch on the
    /// vertical path -- the first vertical drag would kill the label for
    /// the session, hover and keyboard shows alike. The horizontal drag
    /// has no counterpart here: its own completed handler applies this
    /// same rule in its body.
    /// </summary>
    internal void EndRunLabelDrag() => ApplyLabelPhase(_labelRules.DragEnded());

    /// <summary>
    /// Deactivation is a hide rule for the run label: whatever run it was
    /// naming belongs to a window the user is no longer looking at. It
    /// goes through the machine, not a bare element hide -- a bare hide
    /// leaves the phase pending and its timers armed, and the label
    /// surfaces again on a window nobody is looking at.
    /// </summary>
    internal void CloseRunLabelForDeactivation()
        => ApplyLabelPhase(_labelRules.Deactivated());

    /// <summary>
    /// A layout switch was requested. A hide rule in its own right: the
    /// label is anchored to this strip's arrangement, and the switch is
    /// about to replace both.
    /// </summary>
    internal void CloseRunLabelForLayoutSwitch()
        => ApplyLabelPhase(_labelRules.LayoutSwitchRequested());

    private void OnRunMemberPointerEntered(TabModel tab)
    {
        if (tab.Group is not { } group || group.IsCollapsed) return;
        if (_chipByGroup.ContainsKey(group)) return;
        _labelGroup = group;
        ApplyLabelPhase(_labelRules.HoverEnter());
    }

    /// <summary>
    /// The one door every label event goes through. Each target phase
    /// names its own timer work and element op, so the translation is
    /// total: nothing pending survives into a phase that should not carry
    /// it, and Idle reads the machine's cut flag -- a drag start hides as
    /// a cut, everything else as a fade.
    /// </summary>
    private void ApplyLabelPhase(TabRunLabelRules.Phase to)
    {
        _labelPhase = to;
        switch (to)
        {
            case TabRunLabelRules.Phase.HoverPending:
                _labelGraceTimer.Stop();
                _labelKeyboardTimer.Stop();
                _labelShowTimer.Stop();
                _labelShowTimer.Start();
                break;
            case TabRunLabelRules.Phase.Shown:
                _labelShowTimer.Stop();
                _labelGraceTimer.Stop();
                _labelKeyboardTimer.Stop();
                if (_labelRules.KeyboardShown) _labelKeyboardTimer.Start();
                ShowRunLabel();
                break;
            case TabRunLabelRules.Phase.GracePending:
                _labelShowTimer.Stop();
                _labelKeyboardTimer.Stop();
                _labelGraceTimer.Stop();
                _labelGraceTimer.Start();
                break;
            case TabRunLabelRules.Phase.Idle:
                _labelShowTimer.Stop();
                _labelGraceTimer.Stop();
                _labelKeyboardTimer.Stop();
                _runLabel?.Hide(_labelRules.CutOnHide);
                break;
        }
    }

    private void ShowRunLabel()
    {
        if (_runLabel is null || _labelGroup is not { } group) return;
        var members = _manager.MembersOf(group);
        if (members.Count == 0) return;
        // The label spans the run: anchored at the head, sized by the
        // tail. Both elements come from the strip's own map -- the same
        // identity lookup TabElement serves -- never from strip order.
        if (_itemByModel.TryGetValue(members[0], out var head)
            && _itemByModel.TryGetValue(members[^1], out var tail))
            _runLabel.ShowFor(group, members.Count, head, tail);
    }

    // -----------------------------------------------------------------
    // Chips: the horizontal strip's rendering of a collapsed run. A chip
    // is a real TabViewItem so TabView treats it as strip inventory -- it
    // occupies a slot, it drags, it parks the strip selection -- but it
    // is not a tab: it closes nothing (IsClosable=false), it carries the
    // GROUP on Tag, and its DataContext stays null on purpose. The strip
    // used to read its tab order out of DataContext; a chip there would
    // masquerade as a tab.
    // -----------------------------------------------------------------

    private void AddGroupChip(TabGroup group)
    {
        if (_chipByGroup.ContainsKey(group)) return;

        // Swatch, title, member count, chevron: the vertical header row's
        // four-part language, laid out horizontally. Collapsed points
        // right and there is no expanded glyph because a chip only exists
        // while collapsed -- expanding retires it and the members render
        // as themselves.
        var swatch = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
            Text = group.Title,
        };
        var count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0.7,
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
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(swatch);
        headerPanel.Children.Add(title);
        headerPanel.Children.Add(count);
        headerPanel.Children.Add(chevron);

        var chip = new TabViewItem
        {
            Header = headerPanel,
            Content = null,
            IsClosable = false,
            Tag = group,
            // While the run is folded, the chip IS the run's surface: the
            // group commands live here. BuildGroupMenu is host-agnostic
            // (the vertical header uses the same builder), and every item
            // routes through the router, so a commanded rename, color,
            // collapse or close announces exactly like the vertical's --
            // a direct manager call here would move state silently.
            ContextFlyout = TabContextMenuBuilder.BuildGroupMenu(
                _manager,
                group,
                _dialogs,
                requestCollapseGroup: _router.RequestCollapseGroup,
                requestDissolveGroup: _router.RequestDissolveGroup,
                requestCloseGroup: _router.RequestCloseGroup,
                requestRenameGroup: _router.RequestRenameGroup,
                requestColorGroup: _router.RequestColorGroup),
        };
        _chipByGroup[group] = new ChipVisuals(chip, title, count, swatch, chevron);
        TabViewControl.TabItems.Add(chip);
        RefreshChip(group);
        // The mint is the swap's appear-hand: the chip takes the slots
        // its members just left, so it arrives on the fade token rather
        // than snapping into a strip the eye was reading a moment ago.
        FadeInAppearing(chip);
    }

    private void RemoveGroupChip(TabGroup group)
    {
        if (!_chipByGroup.Remove(group, out var chip)) return;
        // The retirement is the swap's other half: the run's members
        // re-enter through the rebuild that follows, and that pass fades
        // them in. The removal itself stays immediate -- TabView has no
        // item exit, and a retiring chip must not linger.
        _swapFadePending = true;
        TabViewControl.TabItems.Remove(chip.Item);
    }

    /// <summary>
    /// Chip presence, the pass before every order pass: build the chips
    /// the projection names, retire the ones it dropped, and re-read every
    /// survivor. The standing subscriptions keep this current event by
    /// event; it stays a pass so a rebuild and the switch-on seam refresh
    /// can re-derive presence outright instead of trusting the events.
    /// </summary>
    /// <summary>
    /// Listen to every group the manager holds, collapsed or not, and drop
    /// the ones it no longer does.
    ///
    /// The hook used to be minted with the chip, which made it circular:
    /// an expanded run has no chip, so nothing was listening when its
    /// collapse bit flipped, so no chip was ever minted for it. The strip
    /// went on rendering the run's members until some unrelated tab event
    /// or a layout switch's <see cref="RefreshSeam"/> re-derived presence
    /// -- which is why a collapse looked like it only took hold on the way
    /// INTO horizontal, showing the run expanded for the length of the
    /// switch and folding it at the landing (issue #871). Chip presence is
    /// a projection of the collapse bit, so the listener has to outlive
    /// the chip rather than be minted by it.
    ///
    /// Idempotent, and cheap enough to ride the presence pass: the set is
    /// as long as the window has groups.
    /// </summary>
    private void ReconcileGroupHooks()
    {
        foreach (var group in _hookedGroups.ToArray())
        {
            if (_manager.Groups.Contains(group)) continue;
            group.PropertyChanged -= OnGroupPropertyChanged;
            _hookedGroups.Remove(group);
        }
        foreach (var group in _manager.Groups)
            if (_hookedGroups.Add(group))
                group.PropertyChanged += OnGroupPropertyChanged;
    }

    /// <summary>
    /// Stop listening to every group on the way out. The manager outlives
    /// this strip (a layout switch keeps both hosts, and a window teardown
    /// disposes them in an order this control does not own), so a live
    /// subscription would hold the whole strip alive through the group.
    /// </summary>
    private void UnhookAllGroups()
    {
        foreach (var group in _hookedGroups)
            group.PropertyChanged -= OnGroupPropertyChanged;
        _hookedGroups.Clear();
    }

    private void ReconcileChips()
    {
        // Presence first needs to be able to HEAR: hooking here means the
        // pass that reads the collapse bit is also the pass that arms the
        // listener for the next change to it.
        ReconcileGroupHooks();

        var desired = new HashSet<TabGroup>();
        foreach (var row in TabStripProjection.HorizontalRows(_manager))
            if (row is TabStripProjection.HorizontalRow.Chip { Group: { } group })
                desired.Add(group);

        // The swap's mutations raise SelectionChanged -- hiding the
        // previously-active member removes the SELECTED item, and TabView
        // re-targets its selection synchronously. Suppressed here, the
        // raise cannot re-enter Activate and chain a second reconcile
        // onto a strip this pass is still mid-swap on. Saved and
        // restored, not disarmed: this pass runs inside
        // RebuildStripFromManager's own fence window on the rebuild path,
        // and a naive disarm there would cut the outer window short (the
        // RemoveGroupChip fence lesson, at the pass that owns the swap).
        var outerSuppress = _suppressSelectionEvent;
        _suppressSelectionEvent = true;
        try
        {
            foreach (var group in _chipByGroup.Keys.ToArray())
                if (!desired.Contains(group))
                {
                    RemoveGroupChip(group);
                    RestoreRunMembers(group);
                }
            foreach (var group in desired)
                if (!_chipByGroup.ContainsKey(group))
                {
                    AddGroupChip(group);
                    HideRunMembers(group);
                }
        }
        finally
        {
            _suppressSelectionEvent = outerSuppress;
        }
        foreach (var group in desired)
            RefreshChip(group);
    }

    /// <summary>
    /// The hide half of the chip swap: a minted chip replaces the run's
    /// rendered members, whose items leave TabItems here. Left in, the
    /// strip holds one more row than the projection names -- presence
    /// skew the order pass must refuse, and its rebuild runs
    /// TabItems.Clear re-entrantly from inside the raising
    /// SelectionChanged, the 0x8000FFFF family. The order pass owns the
    /// last word; this pass owes it consistent presence.
    /// </summary>
    private void HideRunMembers(TabGroup group)
    {
        foreach (var member in _manager.MembersOf(group))
        {
            if (_itemByModel.TryGetValue(member, out var item))
                TabViewControl.TabItems.Remove(item);
        }
    }

    /// <summary>
    /// The restore half: a retiring chip's members re-enter TabItems, in
    /// manager order for now -- the order pass re-seats them. The
    /// retirement armed the swap flag for exactly these rows, and the
    /// restore spends it: each re-entering row fades in, the swap's
    /// appear-hand.
    /// </summary>
    private void RestoreRunMembers(TabGroup group)
    {
        foreach (var member in _manager.MembersOf(group))
        {
            if (_itemByModel.TryGetValue(member, out var item)
                && !TabViewControl.TabItems.Contains(item))
            {
                TabViewControl.TabItems.Add(item);
                if (_swapFadePending) FadeInAppearing(item);
            }
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabGroup group) return;
        if (e.PropertyName == nameof(TabGroup.IsCollapsed))
        {
            // Only the bit changes what the strip HOLDS: expanding retires
            // the chip and the members render as themselves; collapsing
            // mints one when the run does not hold the active tab. Title
            // and color are in-place refreshes and fall through to one.
            // A collapse is a hide rule for the label: the run it named
            // is about to stop being a run of visible members.
            ApplyLabelPhase(_labelRules.Collapsed());
            ReconcileChips();
            ReconcileStripOrder();
        }
        else
        {
            RefreshChip(group);
            // The run's members carry the group's name and color on their own
            // chrome now; the same single-door pass the chip's swatch rides
            // repaints them, so a recolor never leaves a two-tone run.
            RefreshRunRails(group);
        }

        // Both arms move the field, and neither reaches it on its own.
        //
        // The cap and the end bar are placed and inked from
        // UpdateSelectedTabBridge, which rides add, remove, move, activation,
        // resize and the drag's end. A group change is none of those: the
        // active-visible rule keeps the active tab out of the hidden set, so a
        // collapse raises no ActiveTabChanged, and TabViewControl's own size
        // does not move. So a collapse left both bars at the coordinates of the
        // run they used to span -- in the middle of unrelated tabs, since equal
        // width re-lays every remaining tab -- and a recolour left them the old
        // colour, in both cases until something unrelated happened to run the
        // pass. The vertical strip has no equivalent hole because its field
        // rides the reconcile every group change already schedules.
        QueueBridgeUpdate();
    }

    /// <summary>
    /// Re-derive the chrome of every member of one expanded run. The rail
    /// and the tint both read the group, so a group change re-runs the
    /// same per-item pass a selection runs -- one door, no per-element
    /// special cases to forget.
    /// </summary>
    private void RefreshRunRails(TabGroup group)
    {
        foreach (var member in _manager.MembersOf(group))
        {
            if (_itemByModel.TryGetValue(member, out var item)
                && item.Header is StackPanel headerPanel)
            {
                ApplyTabChrome(item, headerPanel, member,
                    ReferenceEquals(member, _manager.ActiveTab));
            }
        }
    }

    /// <summary>
    /// Re-read one chip's renderable state; every group change lands here
    /// once no matter which op moved it, the same single-door shape the
    /// vertical header row uses.
    /// </summary>
    private void RefreshChip(TabGroup group)
    {
        if (!_chipByGroup.TryGetValue(group, out var chip)) return;
        var members = _manager.MembersOf(group).Count;
        chip.Title.Text = group.Title;
        chip.Count.Text = members.ToString();
        // The swatch paints unconditionally: a group has no "no color" state.
        chip.Swatch.Background = TabColorBrush.From(
            TabColorPalette.Background(group.Color, selected: false));
        // A chip IS the folded run, so it takes the field's ground the same
        // way an expanded run's members do -- with the cap and end bar
        // around it, that is what makes it read as a container rather than
        // as a tab with a dot on it. Never the selected brushes: a chip is
        // never the selected item.
        ApplyFieldWashStates(chip.Item);
        // A panel header gives a TabViewItem no name, so the chip is named
        // and statused the way tabs are -- else a screen reader hears how
        // many rows the strip holds and nothing about any of them.
        AutomationProperties.SetName(chip.Item, TabAccessibleText.GroupChipName(group));
        AutomationProperties.SetItemStatus(chip.Item,
            TabAccessibleText.GroupChipStatus(group, members));
    }

    /// <summary>
    /// Chip ink. A group has no "no color" state, so every chip takes the
    /// colored-title path; selected:false because a chip is never the
    /// selected item.
    /// </summary>
    private void RecolorChips()
    {
        foreach (var (group, chip) in _chipByGroup)
        {
            var fg = TabColorBrush.FromPackedRgb(TabColorPalette.ForegroundRgb(
                group.Color, selected: false, _stripBackdropPacked));
            chip.Title.Foreground = fg;
            chip.Count.Foreground = fg;
            chip.Chevron.Foreground = fg;
        }
    }

    /// <summary>
    /// The horizontal consumer of a collapse COMMAND (keyboard chord or
    /// palette), the twin of the vertical strip's command entry: the
    /// manager op plus focus re-homing. The render follows from the
    /// group's INPC raise, so this only has to move focus after the bit
    /// lands -- onto the chip the fold mints, or back onto the active
    /// member's item when the fold touches the run holding it.
    /// </summary>
    internal void CollapseGroupFromCommand(TabGroup group, bool collapsed)
    {
        if (_stripDragActive) return; // the strip stands down under a drag
        if (_manager.Groups.Contains(group) && group.IsCollapsed != collapsed)
            _manager.CollapseGroup(group, collapsed);
        if (_chipByGroup.TryGetValue(group, out var chip))
            chip.Item.Focus(FocusState.Programmatic);
        else
            SelectActive();
    }

    private void MoveItem(TabModel tab, int to)
    {
        if (!_itemByModel.TryGetValue(tab, out var item)) return;
        var current = TabViewControl.TabItems.IndexOf(item);
        // The event's index counts TABS; the strip's slots also hold
        // chips, so the raw `to` is not a slot index and must not reach a
        // strip comparison. The projection translates it, and answers -1
        // for a tab its run's chip hides: no slot equals -1, so the
        // in-place guard declines that case and the reconcile below
        // re-derives the strip -- the chip stands, the stray slot goes.
        var slot = TabStripProjection.ModelIndexToVisibleIndex(_manager, to);
        // A drag drop raises this event after TabView has applied the
        // move itself, so the common case is in-place, and re-inserting
        // at the index the item already occupies churns the strip for
        // nothing. But "in-place" is measured against the moved item only
        // -- Normalize can have repaired the rest of the strip around it
        // (group re-gather), so the early return still runs the reconcile.
        if (current == slot)
        {
            ReconcileStripOrder();
            return;
        }
        if (slot >= 0)
        {
            TabViewControl.TabItems.Remove(item);
            TabViewControl.TabItems.Insert(slot, item);
        }
        // _paneHostContainer order does not matter — Visibility picks
        // the active one. No reorder needed there.

        // The event's indices are the raw op's, and Normalize may have
        // repaired further than the op asked; the reconcile re-derives
        // the strip from the manager's state and owns the last word.
        ReconcileStripOrder();
    }

    /// <summary>A row's trace name: which kind, and whose. A chip and a
    /// tab can carry the same title, so the kind prefix is the identity.
    /// </summary>
    private static string RowName(object? row) => row switch
    {
        TabViewItem { Tag: TabGroup group } => "chip:" + group.Title,
        TabViewItem { Tag: TabModel tab } => "tab:" + tab.EffectiveTitle,
        _ => row?.GetType().Name ?? "null",
    };

    /// <summary>
    /// Bring TabItems back into the order the projection holds, chips
    /// included -- a chip occupies a slot, so the flat tab list stopped
    /// describing the strip the run chips landed. The repair for every
    /// seam where the two can disagree: Normalize's silent relocations
    /// after a raw move, TabView's own reorder that the manager refused
    /// or clamped, and chip presence drift. Zero ops when they already
    /// agree, so the calls on the happy paths cost one comparison per
    /// row.
    /// </summary>
    private void ReconcileStripOrder()
    {
        var repairedRows = 0;
        var rebuilt = false;
        var actualOrder = new List<string>();
        // ListView drops its selection when the selected item is removed
        // and does not restore it on re-insert; live, that drop surfaces
        // as an activation of whatever TabView picked instead. A repair
        // must never read as a tab switch.
        _suppressSelectionEvent = true;
        try
        {
            // The desired slot sequence off the projection: items AND
            // chips. A row this host holds no element for is skew an
            // order pass cannot repair -- counts can agree while a row is
            // missing on both sides -- so the miss flag and the count
            // funnel into one refusal, and the refusal is the rebuild.
            var desired = new List<object>(TabViewControl.TabItems.Count);
            var missing = false;
            foreach (var row in TabStripProjection.HorizontalRows(_manager))
            {
                switch (row)
                {
                    case TabStripProjection.HorizontalRow.Chip { Group: { } group }:
                        if (_chipByGroup.TryGetValue(group, out var chip))
                            desired.Add(chip.Item);
                        else
                            missing = true;
                        break;
                    case TabStripProjection.HorizontalRow.Item { Tab: { } tab }:
                        if (_itemByModel.TryGetValue(tab, out var item))
                            desired.Add(item);
                        else
                            missing = true;
                        break;
                }
                if (missing) break;
            }
            if (missing || TabViewControl.TabItems.Count != desired.Count)
                throw new InvalidOperationException(
                    "TabHost reconcile: the strip and the projection hold " +
                    "different rows. Order is repairable; presence skew " +
                    "is a wiring bug, not a projection.");
            // The strip's order as the pass found it, taken before any
            // repair: the repair trace's "actual" half must describe what
            // was wrong, not the already-fixed strip.
            if (TabDragTrace.Enabled)
                actualOrder.AddRange(TabViewControl.TabItems.Select(RowName));
            for (var i = 0; i < desired.Count; i++)
            {
                if (ReferenceEquals(TabViewControl.TabItems[i], desired[i])) continue;
                // Counts agreeing is not presence agreeing: a stray element
                // no desired row names removes nothing here and would ride
                // out the pass stranded with the repair flag claiming
                // health. Same refusal as above, same funnel.
                if (!TabViewControl.TabItems.Remove(desired[i]))
                    throw new InvalidOperationException(
                        "TabHost reconcile: the strip holds a row the " +
                        "projection does not name. Order is repairable; " +
                        "presence skew is a wiring bug, not a projection.");
                TabViewControl.TabItems.Insert(i, desired[i]);
                repairedRows++;
            }
            if (repairedRows > 0)
                TabDragTrace.Line($"COMMIT reconcile repaired={repairedRows} " +
                    $"desired=[{string.Join(",", desired.Select(RowName))}] " +
                    $"actual=[{string.Join(",", actualOrder)}]");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            // Skew is not producible today: the subscriptions and the
            // handlers above hold presence in step. The projection keeps
            // its refusal as the contract; here a terminal's strip must
            // not die, so rebuild from the manager.
            _log.LogReconcileFailed(ex);
            // The skew is named in the log; this line is the trace half
            // of the same fact -- the rebuild is the last resort, and a
            // rung that lands one must be able to see it fire.
            TabDragTrace.Line($"DIAG reconcile failed items={TabViewControl.TabItems.Count} " +
                $"ex={ex.Message}");
            // The rebuild may land inside MUXC's still-open container
            // state (the raise that led here came from a strip commit).
            // The retry yields off the foreign frame; every attempt
            // carries its own saved/restored selection fence and re-reads
            // manager truth at run time.
            ReconcileRetry.Rebuild(
                "horizontal rebuild",
                () =>
                {
                    var outerSuppress = _suppressSelectionEvent;
                    _suppressSelectionEvent = true;
                    try { RebuildStripFromManager(); }
                    finally { _suppressSelectionEvent = outerSuppress; }
                },
                () =>
                {
                    _swapFadePending = false;
                    SelectActive();
                    TabDragTrace.Line("COMMIT reconcile rebuilt from manager");
                },
                m => TabDragTrace.Line($"DIAG {m}"),
                next => global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
                    global::Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                    () => next()));
            return;
        }
        finally
        {
            _suppressSelectionEvent = false;
        }
        // The swap pass ends here, whatever it did: a chip that retired
        // without a rebuild (a run's last member closed; nothing
        // re-entered) must not fade some later pass's re-adds. One
        // clear, owned by the pass, so the flag can never outlive the
        // retirement that set it.
        _swapFadePending = false;
        if (repairedRows > 0 || rebuilt) SelectActive();
    }

    /// <summary>
    /// The reconcile's last resort: rebuild TabItems in projection order
    /// from the rows this host owns -- chips included, else a rebuild
    /// would silently drop every group's chip from the strip -- repairing
    /// skew in both directions (a row the strip lost, an element the
    /// manager does not hold).
    /// </summary>
    private void RebuildStripFromManager()
    {
        // Presence first: the walk below reads chips this host must hold.
        ReconcileChips();
        // The swap's appear-hand is measured against the slots this pass
        // started with: a row the strip did not hold is one the chip <->
        // members swap just revealed, and it fades in. Every other
        // re-add is the rebuild's own repair, and a repair is not motion.
        var held = new HashSet<object>(TabViewControl.TabItems);
        TabViewControl.TabItems.Clear();
        foreach (var row in TabStripProjection.HorizontalRows(_manager))
        {
            switch (row)
            {
                case TabStripProjection.HorizontalRow.Chip { Group: { } group }
                    when _chipByGroup.TryGetValue(group, out var chip):
                    TabViewControl.TabItems.Add(chip.Item);
                    break;
                case TabStripProjection.HorizontalRow.Item { Tab: { } tab }
                    when _itemByModel.TryGetValue(tab, out var item):
                    TabViewControl.TabItems.Add(item);
                    if (_swapFadePending && !held.Contains(item))
                        FadeInAppearing(item);
                    break;
            }
        }
    }

    private void SelectActive()
    {
        // Active-tab PaneHost visibility is owned by MainWindow's
        // shared container. This method only syncs the TabView strip
        // selection.
        if (!_itemByModel.TryGetValue(_manager.ActiveTab, out var item)) return;
        // Reverse sync never targets a chip. Under the Edge-135 walk the
        // active tab always owns an item -- its collapsed run shows the
        // member, not the chip -- so this is a tripwire, not a live
        // branch: a chip holding the selection would make every later
        // activation read as an expand request.
        if (item.Tag is TabGroup) return;

        // Re-apply tab header fills for selection and preset colors.
        foreach (var (model, viewItem) in _itemByModel)
        {
            if (viewItem.Header is StackPanel headerPanel)
            {
                var isSelected = ReferenceEquals(model, _manager.ActiveTab);
                ApplyTabChrome(viewItem, headerPanel, model, isSelected);
            }
        }

        // Active/inactive foreground must follow selection too, else the
        // newly-deselected tab keeps the active brush and goes invisible
        // against its inactive background (#342).
        RecolorTabText();

        if (ReferenceEquals(TabViewControl.SelectedItem, item)) return;
        _suppressSelectionEvent = true;
        TabViewControl.SelectedItem = item;
        _suppressSelectionEvent = false;
        OnSelectionLanded(item);
    }

    /// <summary>
    /// The label's selection rule, run only when the strip's selection
    /// actually landed somewhere new. One door for every path that moves
    /// the active tab -- TabView keyboard selection, move_tab, Ctrl+Tab,
    /// MRU, a click. Reverse sync suppresses SelectionChanged, so the
    /// event cannot carry this; this is what always runs. The old run's
    /// label hides (the selection hide rule); a landing INSIDE an
    /// expanded run is the keyboard show, 1200ms then faded. A click on
    /// a member takes that restart path rather than hover's: the phase
    /// at click time is still HoverPending, so the landing cancels the
    /// armed 500ms and shows at once as the courtesy. Only a label
    /// already Shown is left alone -- re-selecting into its run must not
    /// stretch the courtesy into a stuck label.
    /// </summary>
    private void OnSelectionLanded(TabViewItem item)
    {
        if (_runLabel is null) return;
        var active = _manager.ActiveTab;
        if (active?.Group is { } group && !group.IsCollapsed
            && !_chipByGroup.ContainsKey(group))
        {
            if (!ReferenceEquals(group, _labelGroup)
                || _labelPhase != TabRunLabelRules.Phase.Shown)
            {
                ApplyLabelPhase(_labelRules.SelectionChanged());
                _labelGroup = group;
                ApplyLabelPhase(_labelRules.KeyboardRequested());
            }
        }
        else if (_labelPhase != TabRunLabelRules.Phase.Idle)
        {
            ApplyLabelPhase(_labelRules.SelectionChanged());
        }
    }

    private static readonly SolidColorBrush TransparentHeaderSelected =
        new(Microsoft.UI.Colors.Transparent);

    private SolidColorBrush? _selectedTabFillBrush;
    private uint _stripBackdropPacked = 0x0C0C0C;

    /// <summary>
    /// The field the active tab is painted with, and the seam cover under it.
    /// One instance for both, so the tab and the strip of pane border it
    /// joins to cannot disagree while the fill is settling.
    /// </summary>
    private readonly ActiveFieldFill _field = new();

    /// <summary>The strip's own ground, as a colour: what the field grows out of.</summary>
    private Windows.UI.Color StripGround() => Windows.UI.Color.FromArgb(
        0xFF,
        (byte)(_stripBackdropPacked >> 16),
        (byte)(_stripBackdropPacked >> 8),
        (byte)_stripBackdropPacked);

    /// <summary>
    /// Re-apply every tab header background after a preset-color or
    /// selected-fill change.
    /// </summary>
    internal void RefreshTabColors()
    {
        TabViewItem? selectedItem = null;
        foreach (var (model, viewItem) in _itemByModel)
        {
            if (viewItem.Header is StackPanel headerPanel)
                ApplyTabChrome(viewItem, headerPanel, model, ReferenceEquals(model, _manager.ActiveTab));
            if (ReferenceEquals(model, _manager.ActiveTab))
                selectedItem = viewItem;
        }
        RecolorTabText();
        // The pinned shape survives a theme swap, but the pass is cheap
        // and this is the one place that re-derives every item's chrome
        // from scratch; leaving the shape out of it would make the theme
        // refresh the one pass that can disagree with the rest.
        ApplyPinZoneChrome();
        RefreshTabViewTheme();
        if (selectedItem is not null)
            NudgeTabViewItemVisual(selectedItem);
        UpdateSelectedTabBridge();
    }

    /// <summary>
    /// Where the selected tab sits horizontally, and what colour it is
    /// filled with. Raised whenever either could have moved.
    /// </summary>
    /// <remarks>
    /// MainWindow uses this to cover the active pane's top border for
    /// exactly that span, so the selected tab reads as continuous with the
    /// terminal rather than having a line ruled between them. It is
    /// MainWindow's to draw and not this control's: the border belongs to
    /// the pane, which lives in the row below, and a cover drawn from up
    /// here has to overhang its own parent to reach it -- where it is
    /// clipped and never appears. Width is zero when there is nothing to
    /// cover.
    /// </remarks>
    internal event Action<double, double, Brush?>? SelectedTabSeamChanged;

    /// <summary>
    /// Re-raise the seam position. For callers that change something the
    /// strip cannot observe -- a layout switch does not resize it or move
    /// its selection, so nothing here would fire on its own.
    /// </summary>
    /// <remarks>
    /// Also arms the layout retry. A strip arriving from a cross-fade can
    /// still be settling, and unlike the not-yet-arranged case its offsets
    /// are non-zero, so the placement looks valid and would never be
    /// revisited.
    /// </remarks>
    /// <summary>
    /// The strip's own inventory, in slot order: what TabItems holds, not
    /// what the projection says it should. The difference is the whole
    /// point -- a switch that flashes a collapsed run expanded holds the
    /// members here for a few frames while the projection has said "chip"
    /// throughout.
    /// </summary>
    public IReadOnlyList<Testing.TestSeamStripRow> TestSeamRows(FrameworkElement root)
    {
        var modelByItem = new Dictionary<TabViewItem, TabModel>(_itemByModel.Count);
        foreach (var (tab, item) in _itemByModel) modelByItem[item] = tab;

        var rows = new List<Testing.TestSeamStripRow>(TabViewControl.TabItems.Count);
        foreach (var entry in TabViewControl.TabItems)
        {
            if (entry is not TabViewItem item) continue;
            if (item.Tag is TabGroup group)
            {
                rows.Add(Testing.TestSeamStripRowMeasure.Row(
                    root, item, "chip", group.Title, group.Title, active: false));
                continue;
            }
            if (!modelByItem.TryGetValue(item, out var tab)) continue;
            rows.Add(Testing.TestSeamStripRowMeasure.Row(
                root, item,
                tab.IsPinned ? "pinned" : "tab",
                tab.EffectiveTitle,
                tab.Group?.Title,
                ReferenceEquals(tab, _manager.ActiveTab)));
        }
        return rows;
    }

    internal void RefreshSeam()
    {
        // Belt and braces. The standing subscriptions hold the strip in
        // step event by event -- the manager raises whether or not this
        // host is the visible layout -- so this switch-on pass should
        // find nothing. It exists for drift a missed subscription cannot
        // confess to: re-derive presence, order, and selection from the
        // projection before the seam reads a slot.
        ReconcileChips();
        ReconcileStripOrder();
        SelectActive();
        QueueBridgeUpdate();
        ArmBridgeRetry();
    }

    private void UpdateSelectedTabBridge()
    {
        // The field's terminals ride this pass: it is already the one every
        // add, remove, move, activation and resize funnels into, and it
        // already knows how to wait for bounds. A placement of its own
        // would be a second thing to remember on every one of those doors.
        UpdateGroupFieldTerminals();

        if (_stripDragActive)
        {
            // Mid-drag the strip's slots are TabView's reorder preview,
            // not the manager's order; placing from them paints the cover
            // onto a stale slot for the length of the drag. The drop
            // re-places it.
            SelectedTabSeamChanged?.Invoke(0, 0, null);
            return;
        }

        var active = _manager.ActiveTab;
        if (active is null
            || !_itemByModel.TryGetValue(active, out var item)
            || !_field.HasColor)
        {
            SelectedTabSeamChanged?.Invoke(0, 0, null);
            return;
        }

        if (item.ActualWidth <= 0)
        {
            // Not arranged yet. Hide rather than place from a stale offset,
            // and come back on the pass that gives it bounds.
            SelectedTabSeamChanged?.Invoke(0, 0, null);
            ArmBridgeRetry();
            return;
        }

        // Edge-135 keeps the active tab an item even inside a collapsed
        // run -- the run shows the member, not the chip -- so the cover
        // always reads a tab's slot. A chip here would place the cover
        // from a group; hide rather than guess.
        if (item.Tag is TabGroup)
        {
            SelectedTabSeamChanged?.Invoke(0, 0, null);
            return;
        }

        // The tab has bounds, so the retry budget did its job and resets for
        // the next tab that arrives without any.
        _bridgeRetries = 0;

        Windows.Foundation.Point origin;
        try
        {
            origin = item.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // The item is not in the tree yet, or is being pulled out of it
            // (a close, or a drag to another window). COM and null are the
            // shapes XAML interop throws once the tree is already going
            // down, and this runs from a dispatcher callback, where an
            // escaping exception is an unhandled one on the UI thread.
            // The next refresh places it.
            SelectedTabSeamChanged?.Invoke(0, 0, null);
            return;
        }

        // Span the tab's full footprint. The side strokes sit at its outer
        // edges and stop at the strip's bottom, so the border pixels that
        // close the folder's two bottom corners are the ones just outside
        // this span -- not inside it. Insetting by the stroke width instead
        // leaves a pixel of border showing within the tab at each end, and
        // that reads as a notch rather than a corner.
        var left = origin.X;
        var right = origin.X + item.ActualWidth;

        // Clip to the list the tabs scroll inside. Once there are more tabs
        // than fit, the selected one can be scrolled half out of view or
        // right out of it, and its layout offset keeps reporting where the
        // tab would be rather than where it is drawn. Uncovered, that walks
        // the cover along the pane border and rubs out a stretch of it
        // nowhere near the tab.
        if (TabStripViewport() is { } viewport)
        {
            left = Math.Max(left, viewport.Left);
            right = Math.Min(right, viewport.Right);
        }

        var width = right - left;
        if (width <= 0)
        {
            SelectedTabSeamChanged?.Invoke(0, 0, null);
            return;
        }

        // The very brush the tab is painted with, not a fresh one mixed to the
        // same colour. A copy is correct at rest and wrong for the length of
        // every settle, and the frames it is wrong in are the frames where a
        // line appears between the tab and the pane.
        SelectedTabSeamChanged?.Invoke(left, width, _field.Brush);

        // A placement made while the strip is still moving - a
        // bring-into-view settle, an equal-width reflow, a caption-inset
        // resize - reads valid bounds, looks final, and was never
        // revisited. Re-check on the next layout pass whenever the span
        // MOVED; when it stops moving this stops arming, so the chain
        // converges at the fixpoint instead of running forever.
        if (left != _lastSeamLeft || width != _lastSeamWidth)
        {
            _lastSeamLeft = left;
            _lastSeamWidth = width;
            ArmBridgeRetry();
        }
    }

    // The last span a successful placement reported, for the fixpoint above.
    private double _lastSeamLeft = double.NaN;
    private double _lastSeamWidth = double.NaN;

    // -----------------------------------------------------------------
    // The group field, horizontal edition.
    //
    // Same three parts as the vertical strip's, same grammar, same
    // arithmetic (TabGroupField): a wash for the ground, a cap where the
    // run starts and an end bar where it stops. Only the mechanics differ,
    // because the hosts do. Vertically the field is one Border under the
    // rows, which is possible because that strip's rows sit on a canvas
    // this control has no equivalent of; here the wash rides the members'
    // own header brushes (see ApplyTabChrome) and only the two terminals
    // are drawn, on a canvas over the strip.
    //
    // Pooled by index, hidden rather than removed, for the reason the
    // vertical strip's separators are: this runs from the pass every strip
    // mutation already funnels into, and rebuilding N Rectangles per call
    // would put an allocation on every one of them.
    // -----------------------------------------------------------------

    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _fieldTerminals = new();

    /// <summary>
    /// Clearance above the terminals. A tab's top corners are rounded, and
    /// a bar drawn flush with the top pokes out past the curve on both
    /// ends of the run. The bars stay flush with the BOTTOM on purpose:
    /// that edge is where the strip meets the pane, and a terminal rising
    /// off it reads as a wall around the run rather than as a tick mark
    /// floating in the strip.
    /// </summary>
    private const double FieldTerminalTopInset = 3;

    private void UpdateGroupFieldTerminals()
    {
        // Mid-drag the strip's slots are TabView's reorder preview rather
        // than the manager's order, so a run's ends are not where the
        // projection says they are. The drop re-places them -- the same
        // rule the seam cover above follows, for the same reason.
        //
        // Left where they were, rather than collapsed. Falling through to the
        // tail loop below hid every bar for the length of the gesture while
        // the wash on the members stayed, so a run read as a tint with no
        // beginning and no end -- and the vertical field, which returns here,
        // did not do that. The suppression is the same rule in both strips
        // now, which is the only way one gesture means one thing.
        if (_stripDragActive) return;

        var used = 0;
        {
            var rows = TabStripProjection.HorizontalRows(_manager);
            var viewport = TabStripViewport();
            foreach (var run in TabGroupField.Runs(TabGroupField.SlotGroups(rows)))
            {
                if (SlotElement(rows[run.First]) is not { ActualWidth: > 0 } head) continue;
                if (SlotElement(rows[run.Last]) is not { ActualWidth: > 0 } tail) continue;

                double left, right, top, height;
                try
                {
                    var headOrigin = head.TransformToVisual(this).TransformPoint(new Point(0, 0));
                    var tailOrigin = tail.TransformToVisual(this).TransformPoint(new Point(0, 0));
                    left = headOrigin.X;
                    right = tailOrigin.X + tail.ActualWidth;
                    top = headOrigin.Y + FieldTerminalTopInset;
                    height = head.ActualHeight - FieldTerminalTopInset;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or System.Runtime.InteropServices.COMException or NullReferenceException)
                {
                    // The item is not in the tree yet, or is leaving it.
                    continue;
                }

                if (height <= 0) continue;

                // One ink per END, not one per run: the two bars sit on two
                // different slots and those slots need not be wearing the same
                // thing. Only the vertical field can score both against one
                // ground, because there the bars are edges of a Border that IS
                // the field.
                var headInk = TabColorBrush.FromPackedRgb(TabGroupField.TerminalRgbOn(
                    SlotGroundRgb(rows[run.First]), run.Group.Color));
                var tailInk = TabColorBrush.FromPackedRgb(TabGroupField.TerminalRgbOn(
                    SlotGroundRgb(rows[run.Last]), run.Group.Color));
                used = PlaceFieldTerminal(used, left, top, height, headInk, viewport);
                used = PlaceFieldTerminal(
                    used, right - TabGroupField.TerminalThicknessPx, top, height, tailInk, viewport);
            }
        }

        for (var i = used; i < _fieldTerminals.Count; i++)
            _fieldTerminals[i].Visibility = Visibility.Collapsed;
    }


    /// <summary>
    /// What is actually painted where a terminal drawn on <paramref name="row"/>
    /// lands, so the bar can be scored against it.
    ///
    /// The field's wash is the usual answer and the only one the vertical strip
    /// has, but two slots opt out of it: the selected tab, which keeps the
    /// terminal background so it reads as continuous with its pane, and a
    /// member carrying a preset of its own, which keeps that preset. A chip
    /// falls through to the wash, which is correct -- a folded run's one slot
    /// wears the field.
    /// </summary>
    private uint SlotGroundRgb(TabStripProjection.HorizontalRow row)
    {
        if (row is TabStripProjection.HorizontalRow.Item { Tab: { } tab })
        {
            // The branch ORDER is ApplyTabChrome's, and has to be: a preset
            // beats being active there, so an active coloured tab wears the
            // opaque preset and not the terminal background. Asking about
            // active first answered "terminal ground" for a slot painted its
            // own colour -- and for a Red end bar on an active Red member that
            // is a bar scored against near-black, returned unlifted, and
            // painted onto opaque Red. 1:1, which is the exact defect this
            // whole helper was added to remove.
            var selected = ReferenceEquals(tab, _manager.ActiveTab);
            if (tab.Color != TabColor.None)
                return TabColorPalette.EffectiveBackgroundRgb(
                    tab.Color, selected, _stripBackdropPacked);
            if (selected && _selectedTabFillBrush is not null)
                return PackColor(_selectedTabFillBrush.Color);
        }
        return TabGroupField.FieldGroundRgb(_stripBackdropPacked);
    }

    /// <summary>
    /// The element one horizontal slot renders as, or null while the strip
    /// has not built it. A chip counts: it is the folded run's only slot,
    /// so its own ends are the run's ends.
    /// </summary>
    private FrameworkElement? SlotElement(TabStripProjection.HorizontalRow row)
        => row switch
        {
            TabStripProjection.HorizontalRow.Chip { Group: { } group }
                => _chipByGroup.TryGetValue(group, out var chip) ? chip.Item : null,
            TabStripProjection.HorizontalRow.Item { Tab: { } tab }
                => _itemByModel.TryGetValue(tab, out var item) ? item : null,
            _ => null,
        };

    /// <summary>
    /// One bar, placed and clipped to the list the tabs scroll inside -- for
    /// the reason the seam cover is clipped: a run scrolled out of view keeps
    /// reporting the offset it WOULD have, and an unclipped end bar walks off
    /// into the footer.
    /// </summary>
    private int PlaceFieldTerminal(
        int index, double left, double top, double height, Brush ink, Rect? viewport)
    {
        if (viewport is { } clip
            && (left + TabGroupField.TerminalThicknessPx <= clip.Left || left >= clip.Right))
        {
            // Scrolled out of the list entirely: no bar rather than a bar
            // drawn over the header badge or the new-tab button.
            return index;
        }

        Microsoft.UI.Xaml.Shapes.Rectangle bar;
        if (index < _fieldTerminals.Count)
        {
            bar = _fieldTerminals[index];
        }
        else
        {
            bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = TabGroupField.TerminalThicknessPx,
                IsHitTestVisible = false,
            };
            _fieldTerminals.Add(bar);
            GroupFieldHost.Children.Add(bar);
        }

        // A terminal arriving -- a group just created, a run just expanded
        // out of its chip -- comes in on the Fade token, the same hand the
        // chip/member swap already uses. Its POSITION is never animated:
        // this strip's tabs cut to their new slots (there is no gap glide
        // horizontally), and a bar gliding to an end its own tab has
        // already jumped to would trail behind the run it marks.
        var appearing = bar.Visibility != Visibility.Visible;

        bar.Width = TabGroupField.TerminalThicknessPx;
        bar.Height = height;
        bar.Fill = ink;
        bar.Visibility = Visibility.Visible;
        Canvas.SetLeft(bar, left);
        Canvas.SetTop(bar, top);
        if (appearing) FadeInAppearing(bar);
        return index + 1;
    }

    // The list the tab items scroll inside, looked up once out of the
    // TabView's template. Null until the template has been applied.
    private FrameworkElement? _tabListView;

    /// <summary>
    /// Bounds of the scrolling tab list, in this control's coordinates, or
    /// null while the template has not been applied yet.
    /// </summary>
    private Windows.Foundation.Rect? TabStripViewport()
    {
        _tabListView ??= FindDescendantByName(TabViewControl, "TabListView");
        if (_tabListView is not { ActualWidth: > 0 } list) return null;
        HookTabStripScroller(list);

        try
        {
            var tl = list.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Windows.Foundation.Rect(
                tl.X, tl.Y, list.ActualWidth, list.ActualHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return null;
        }
    }

    // The scroller the tab list scrolls inside. A user wheel or drag-scroll
    // moves the drawn tab while its layout offset keeps reporting where the
    // tab would be, and no enumerated event fires - so the cover sits over a
    // stretch of border nowhere near the tab until the next unrelated
    // refresh. ViewChanged re-derives the span, coalesced through the same
    // queue every other refresh uses.
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _tabStripScroller;

    private void HookTabStripScroller(Microsoft.UI.Xaml.UIElement list)
    {
        if (_tabStripScroller is not null) return;
        var node = (Microsoft.UI.Xaml.DependencyObject?)list;
        while (node is not null)
        {
            if (node is Microsoft.UI.Xaml.Controls.ScrollViewer scroller)
            {
                _tabStripScroller = scroller;
                scroller.ViewChanged += OnTabStripScrollerViewChanged;
                return;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
    }

    private void OnTabStripScrollerViewChanged(object? sender, object e)
        => QueueBridgeUpdate();

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { } fe && fe.Name == name) return fe;
            if (FindDescendantByName(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Re-place the seam after the next layout pass, for the calls that land
    /// before the strip has arranged (construction, a tab added or removed, a
    /// drag reorder).
    /// </summary>
    /// <remarks>
    /// A dispatcher hop alone is not enough and the gap it leaves is not
    /// cosmetic. A tab added at run time has no bounds by the time even a Low
    /// priority callback runs, so the placement bails -- and nothing else
    /// fires afterwards, because the strip's own size did not change. The
    /// seam then stays uncovered until something unrelated moves. So the
    /// bail arms a one-shot LayoutUpdated and the placement happens on the
    /// pass that gives the tab its bounds. One-shot rather than a standing
    /// subscription, which fires for every layout pass anywhere in the
    /// window.
    /// </remarks>
    private bool _bridgeUpdateQueued;

    private void QueueBridgeUpdate()
    {
        // Coalesced. A single new tab reaches here from TabAdded and again
        // from ActiveTabChanged, and each pass walks the visual tree twice
        // and re-places the cover, all to the same answer.
        if (_bridgeUpdateQueued) return;
        _bridgeUpdateQueued = true;

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _bridgeUpdateQueued = false;
                UpdateSelectedTabBridge();
            });
    }

    private bool _bridgeRetryArmed;

    /// <summary>
    /// How many times a single placement may re-arm the layout retry before
    /// giving up.
    /// </summary>
    /// <remarks>
    /// The retry exists for a tab that has no bounds yet and gets them on a
    /// later pass. A strip that is collapsed and was never primed reports
    /// zero forever, so without a cap the bail re-arms on every layout pass
    /// anywhere in the window, permanently, and each one re-runs the
    /// placement. Small because the legitimate case settles in one or two
    /// passes; anything past that is the strip having no layout to wait for.
    /// </remarks>
    private const int MaxBridgeRetries = 4;
    private int _bridgeRetries;

    private void ArmBridgeRetry()
    {
        if (_bridgeRetryArmed) return;
        if (Visibility != Visibility.Visible) return;
        if (_bridgeRetries >= MaxBridgeRetries) return;

        _bridgeRetryArmed = true;
        LayoutUpdated += OnBridgeRetryLayout;
    }

    private void OnBridgeRetryLayout(object? sender, object e)
    {
        LayoutUpdated -= OnBridgeRetryLayout;
        _bridgeRetryArmed = false;
        _bridgeRetries++;
        UpdateSelectedTabBridge();
    }

    /// <summary>Force MUXC to re-read TabView/item header resources.</summary>
    private void RefreshTabViewTheme()
    {
        var theme = TabViewControl.RequestedTheme;
        TabViewControl.RequestedTheme = theme == ElementTheme.Light
            ? ElementTheme.Dark
            : ElementTheme.Light;
        TabViewControl.RequestedTheme = theme;
    }

    private static readonly string[] TabViewItemHeaderNormalKeys =
    [
        "TabViewItemHeaderBackground",
        "TabViewItemHeaderBackgroundPointerOver",
        "TabViewItemHeaderBackgroundPressed",
    ];

    private static readonly string[] TabViewItemHeaderSelectedKeys =
    [
        "TabViewItemHeaderBackgroundSelected",
        "TabViewItemHeaderBackgroundSelectedPointerOver",
        "TabViewItemHeaderBackgroundSelectedPressed",
    ];

    /// <summary>
    /// Stroke around the selected tab: the same colour that frames the
    /// active pane, so the tab and the pane it belongs to read as one shape
    /// rather than as two pieces of chrome that happen to touch.
    /// </summary>
    /// <remarks>
    /// The folder shape itself is already in the WinUI template, which sets
    /// the selected item's border thickness to <c>1,1,1,0</c> -- three sides
    /// and nothing along the edge that meets the pane. It is invisible only
    /// because the default brush is transparent, so painting that one brush
    /// is the whole of the effect. Nothing here needs the strip to have a
    /// surface of its own, which is why this works where recolouring the
    /// strip did not: the strip is the window's Mica backdrop.
    /// </remarks>
    private SolidColorBrush? _selectedBorderBrush;

    /// <summary>
    /// Set the stroke colour for the selected tab. Same value MainWindow
    /// gives the active pane border, so the two cannot disagree.
    /// </summary>
    internal void SetAccentColor(Windows.UI.Color color)
    {
        if (_selectedBorderBrush?.Color == color) return;
        _selectedBorderBrush = new SolidColorBrush(color);
        RefreshTabColors();
    }

    /// <summary>
    /// Paint the full TabViewItem handle via per-item header resources.
    /// The inner header panel stays transparent so the pill, close
    /// button chrome, and progress bar share one surface.
    /// </summary>
    private void ApplyTabChrome(
        TabViewItem viewItem, StackPanel headerPanel, TabModel tab, bool selected)
    {
        SolidColorBrush? normalHandle = null;
        SolidColorBrush? selectedHandle = null;
        var washedByField = false;

        if (tab.Color != TabColor.None)
        {
            normalHandle = TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: false));
            selectedHandle = TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: true));
        }
        else
        {
            // The group field's ground, horizontal edition: the wash goes
            // on the members' own header brushes rather than on a rect
            // behind them, because the strip's surface is owned by
            // TabViewBackground and an opaque palette fill would paint
            // straight over anything drawn under the TabView. Ink at 8%,
            // so a bare frame still shows Mica through the run.
            //
            // Only where the tab has no colour of its own: a preset is the
            // more specific statement about that one tab, and washing over
            // it would make a coloured member of a group a shade nobody
            // picked. And only the UNSELECTED brushes: the selected tab
            // keeps the terminal's background in every combination, which
            // is what makes it read as continuous with the pane below it.
            if (tab.Group is not null)
            {
                normalHandle = TabColorBrush.FromPackedArgb(
                    TabGroupField.WashArgb(_stripBackdropPacked));
                washedByField = true;
            }
            if (selected && _selectedTabFillBrush is not null)
            {
                selectedHandle = _selectedTabFillBrush;
            }
        }

        // The active tab does not take the colour, it takes the field's own
        // brush, settled onto that colour. Everything else about this pass is
        // unchanged; what the swap buys is that the seam cover is handed the
        // same instance, so the two are one surface at every frame and not
        // only at rest.
        if (selected && selectedHandle is not null)
        {
            _field.Settle(
                tab,
                selectedHandle.Color,
                StripGround(),
                TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast));
            selectedHandle = _field.Brush;
        }

        ApplyTabViewItemHeaderBrushes(viewItem, normalHandle, selectedHandle);

        // That writes ONE brush into all three unselected states, which is what
        // a preset tint wants and not what the field wants: a washed member
        // whose pointer-over and pressed are identical to its rest state stops
        // answering the pointer, and a run of them reads as disabled. The wash
        // gets its own ramp instead, so hover and press still say something
        // without introducing a colour the field did not choose.
        if (washedByField) ApplyFieldWashStates(viewItem);

        // A tab carrying a preset colour takes that colour's border, the same
        // way its pane does, so the stroke keeps identifying which pane the
        // tab belongs to instead of flattening every tab to the accent.
        SolidColorBrush? selectedBorder = null;
        if (selected)
        {
            selectedBorder = tab.Color != TabColor.None
                ? TabColorBrush.From(TabColorPalette.Border(tab.Color))
                : _selectedBorderBrush;
        }
        SetItemHeaderBrush(viewItem, "TabViewSelectedItemBorderBrush", selectedBorder);

        // The group rail rides every chrome pass, so a join, a leave, or a
        // color change each land on a pass that already runs. Painted
        // through the chip swatch's palette path: a group has no "no
        // color" state, and the rail is the run's identity mark, not a
        // theme choice for the user to override per color.
        if (_railByModel.TryGetValue(tab, out var rail))
        {
            if (tab.Group is { } group)
            {
                rail.Fill = TabColorBrush.From(
                    TabColorPalette.Background(group.Color, selected: false));
                rail.Visibility = Visibility.Visible;
            }
            else
            {
                rail.Visibility = Visibility.Collapsed;
            }
        }

        headerPanel.Background = TransparentHeaderSelected;
    }

    /// <summary>
    /// The pin zone's edge, horizontal: no stroke at all. A pinned tab is
    /// an icon square and a body tab is a full-width tab, and two shapes
    /// side by side need nothing drawn between them -- the same grammar
    /// the vertical band speaks, laid along the strip instead of down it.
    /// Three pins spend one tab's width here where three used to.
    ///
    /// One writer owns the anatomy -- this pass -- so a tab left in the
    /// wrong shape has exactly one place to come from. The predicate reads
    /// each tab's own flag rather than the prefix length: during a drag
    /// the strip's order is TabView's preview, and a tab's shape must
    /// follow what it IS, not where the preview currently has it.
    /// </summary>
    private void ApplyPinZoneChrome()
    {
        foreach (var (model, item) in _itemByModel)
            ApplyPinnedTabAnatomy(item, model);
    }

    /// <summary>
    /// The width a pinned tab spends: one icon square plus the inset the
    /// vertical rail gives the same square, which is why the two layouts
    /// collapse a pin to the same size. Both bounds, not one: TabView's
    /// Equal mode writes Width on every item it holds, and layout clamps
    /// that Width against Min/Max -- so the clamp is the only way to say
    /// "this slot is fixed" without fighting the strip's own sizing pass
    /// every time an item is added.
    /// </summary>
    private const double PinnedTabInset = 4;
    private const double PinnedTabWidth = TabPinBand.ChipSize + 2 * PinnedTabInset;

    /// <summary>
    /// One tab's pinned anatomy. Pinned: an icon square, its title on the
    /// tooltip, no close button -- a 48px slot has no room for one, and a
    /// pinned tab is not meant to be closed by a stray click anyway; the
    /// context menu and the close keybind still take that decision.
    /// Unpinned: everything back, including the width the strip shares out.
    /// </summary>
    private void ApplyPinnedTabAnatomy(TabViewItem item, TabModel tab)
    {
        var pinned = tab.IsPinned;
        item.MinWidth = pinned ? PinnedTabWidth : 0;
        item.MaxWidth = pinned ? PinnedTabWidth : double.PositiveInfinity;
        item.IsClosable = !pinned;
        // The title the square gives up rides the tooltip in full. An
        // unpinned tab shows its label, so it is owed only what the hover
        // adds -- the directory, or the whole label when the header has
        // had to trim it.
        ToolTipService.SetToolTip(item, pinned
            ? tab.TooltipText
            : tab.HoverText ?? (_headerTextByModel.TryGetValue(tab, out var header) && header.IsTextTrimmed
                ? tab.TooltipText
                : null));

        if (!_iconRowByModel.TryGetValue(tab, out var row)) return;
        // Centred in the square rather than run from the leading edge:
        // the row is the only thing left in the slot.
        row.HorizontalAlignment = pinned
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;
        foreach (var child in row.Children)
        {
            switch (child)
            {
                case TabIconPresenter icon:
                    icon.Margin = new Thickness(0, 0, pinned ? 0 : 6, 0);
                    // On the square the presenter is the whole item, and the
                    // nearest tooltip wins: hand the hover to the item's.
                    icon.ShowsTooltip = !pinned;
                    break;
                case TextBlock title:
                    title.Visibility = pinned || tab.IsHome ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case FontIcon home when home.Glyph == HomeGlyph:
                    home.Visibility = pinned || !tab.IsHome ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case FontIcon bell:
                    // The bell keeps its badge on a pinned square; what it
                    // gives up is the gap before a title that is not there.
                    bell.Margin = new Thickness(pinned ? 0 : 6, 0, 0, 0);
                    break;
            }
        }
    }

    /// <summary>
    /// The three unselected header states of one item that sits on a field,
    /// painted from one ink at three strengths.
    ///
    /// One place, because the chip and an expanded member are the same surface
    /// wearing the same wash: two sites deriving it separately is how the
    /// folded and unfolded readings of a run drift apart.
    /// </summary>
    private void ApplyFieldWashStates(TabViewItem item)
    {
        SetItemHeaderBrush(item, "TabViewItemHeaderBackground",
            TabColorBrush.FromPackedArgb(TabGroupField.WashArgb(_stripBackdropPacked)));
        SetItemHeaderBrush(item, "TabViewItemHeaderBackgroundPointerOver",
            TabColorBrush.FromPackedArgb(TabGroupField.WashArgbAt(
                _stripBackdropPacked, TabGroupField.WashHoverAlpha)));
        SetItemHeaderBrush(item, "TabViewItemHeaderBackgroundPressed",
            TabColorBrush.FromPackedArgb(TabGroupField.WashArgbAt(
                _stripBackdropPacked, TabGroupField.WashPressedAlpha)));
    }

    private static void ApplyTabViewItemHeaderBrushes(
        TabViewItem item, SolidColorBrush? normal, SolidColorBrush? selected)
    {
        foreach (var key in TabViewItemHeaderNormalKeys)
            SetItemHeaderBrush(item, key, normal);
        foreach (var key in TabViewItemHeaderSelectedKeys)
            SetItemHeaderBrush(item, key, selected);
    }

    private static void SetItemHeaderBrush(TabViewItem item, string key, SolidColorBrush? brush)
    {
        if (brush is not null)
            item.Resources[key] = brush;
        else
            item.Resources.Remove(key);
    }

    /// <summary>
    /// MUXC caches TabViewItem header brushes until selection toggles.
    /// </summary>
    private void NudgeTabViewItemVisual(TabViewItem item)
    {
        if (!ReferenceEquals(TabViewControl.SelectedItem, item)) return;
        _suppressSelectionEvent = true;
        try
        {
            TabViewControl.SelectedItem = null;
            TabViewControl.SelectedItem = item;
        }
        finally { _suppressSelectionEvent = false; }
    }

    private SolidColorBrush TabColorForegroundBrush(TabModel tab, bool selected)
    {
        var packed = TabColorPalette.ForegroundRgb(
            tab.Color, selected, _stripBackdropPacked);
        return TabColorBrush.FromPackedRgb(packed);
    }

    private static void ApplyHeaderRowForeground(StackPanel iconRow, SolidColorBrush fg)
    {
        foreach (var child in iconRow.Children)
        {
            switch (child)
            {
                case TextBlock tb:
                    tb.Foreground = fg;
                    break;
                case FontIcon fi:
                    fi.Foreground = fg;
                    break;
                case TabIconPresenter presenter:
                    presenter.Foreground = fg;
                    if (presenter.Content is FontIcon glyph)
                        glyph.Foreground = fg;
                    break;
            }
        }
    }

    private static void ClearHeaderRowForeground(StackPanel iconRow)
    {
        foreach (var child in iconRow.Children)
        {
            switch (child)
            {
                case TextBlock tb:
                    tb.ClearValue(TextBlock.ForegroundProperty);
                    break;
                // The bell is the one glyph whose untagged ink is not the
                // inherited foreground: it is born accent-tinted, so
                // clearing it would strand it theme-coloured for life.
                case FontIcon bell when bell.Glyph == BellGlyph:
                    bell.Foreground = BellAccentBrush();
                    break;
                // The idle moon is born muted for the same reason: the
                // clear pass must put its quiet ink back, not primary.
                case FontIcon idle when idle.Glyph == IdleGlyph:
                    idle.Foreground = IdleGlyphBrush();
                    break;
                case FontIcon fi:
                    fi.ClearValue(FontIcon.ForegroundProperty);
                    break;
                case TabIconPresenter presenter:
                    presenter.ClearValue(ForegroundProperty);
                    if (presenter.Content is FontIcon glyph)
                        glyph.ClearValue(FontIcon.ForegroundProperty);
                    break;
            }
        }
    }

    /// <summary>
    /// Accent brush for the per-tab bell indicator glyph. Resolved against
    /// the live resources each call so it tracks runtime theme changes.
    /// </summary>
    private static Brush BellAccentBrush()
    {
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var c)
            && c is Windows.UI.Color color)
            return new SolidColorBrush(color);
        return new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
    }

    /// <summary>
    /// Muted brush for the idle moon. Secondary text colour, not the
    /// accent: sleeping is a rest state, not something demanding a look.
    /// Resolved against live resources each call so it tracks theme
    /// changes, same contract as <see cref="BellAccentBrush"/>.
    /// </summary>
    private static Brush IdleGlyphBrush()
    {
        if (Application.Current.Resources.TryGetValue(
                "TextFillColorSecondaryBrush", out var b) && b is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>
    /// Whether the moon is shown for this tab right now. The bell owns
    /// the badge: while one is up the moon stays hidden even if IsIdle
    /// has not been recomputed yet, so the slot never shows both.
    /// </summary>
    private static bool IdleBadgeVisible(TabModel tab)
        => tab.IsIdle && !tab.BellRinging;

    /// <summary>
    /// The idle dim: icon and title fade to <see cref="IdleOpacity"/>.
    /// Shared by the build pass and the IsIdle property arm so a tab
    /// created already-idle (a restore landing past the threshold)
    /// starts dimmed instead of flashing bright first.
    /// </summary>
    /// <summary>
    /// Paint (or release) the home glyph in the same ink the header text
    /// takes. A home tab draws no text, so the glyph is the thing an ink
    /// pass has to reach or the tab keeps the old theme's colour.
    /// </summary>
    private static void SetHomeGlyphForeground(StackPanel? iconRow, Brush? ink)
    {
        if (iconRow is null) return;
        foreach (var child in iconRow.Children)
        {
            if (child is FontIcon glyph && glyph.Glyph == HomeGlyph)
            {
                if (ink is null) glyph.ClearValue(IconElement.ForegroundProperty);
                else glyph.Foreground = ink;
                return;
            }
        }
    }

    private static void ApplyIdleInk(TabIconPresenter icon, TextBlock title, TabModel tab)
    {
        var opacity = tab.IsIdle ? IdleOpacity : 1.0;
        icon.Opacity = opacity;
        title.Opacity = opacity;
    }

    /// <summary>
    /// Route a per-tab "Move Tab to New Window" click back to the
    /// owning <see cref="MainWindow"/>. TabHost is a UserControl with
    /// no direct MainWindow reference; <see cref="App.WindowsByRoot"/>
    /// is keyed by <see cref="XamlRoot"/>, so the lookup is O(1).
    /// </summary>
    private void RequestDetachToNewWindow(TabModel tab)
        => TabWindowActions.DetachToNewWindow(XamlRoot, tab);

    private SnapZoneSource GetSnapSource()
        => TabWindowActions.GetSnapSource(XamlRoot);

    private void DetachWithZone(TabModel tab, Ghostty.Core.Tabs.SnapZone zone)
        => TabWindowActions.DetachWithZone(XamlRoot, tab, zone);

    /// <summary>
    /// Wire the owning window into <see cref="NewTabButton"/> so its
    /// click handlers can call <see cref="MainWindow.OpenProfile"/>.
    /// Called by <see cref="MainWindow"/> immediately after constructing
    /// this instance. Option (b): TabHost has no MainWindow field, so
    /// the window pushes itself in rather than TabHost pulling via
    /// App.WindowsByRoot (which is not yet populated at ctor time).
    /// </summary>
    internal void AttachOwner(MainWindow owner)
    {
        NewTabButton.Owner = owner;
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewItem item)
        {
            // Chips are IsClosable=false, so a close request can only
            // name a tab; a chip here would mean close chrome leaked
            // onto a group.
            if (item.Tag is TabGroup) return;
            foreach (var (model, vi) in _itemByModel)
            {
                if (vi == item) { await RequestCloseTabAsync(model); return; }
            }
        }
    }

    /// <summary>
    /// Single entry point for every "close this tab" path: per-tab
    /// X button, middle-click, context-menu Close, and the keyboard
    /// chord (via <see cref="MainWindow"/>'s accelerator handler).
    /// Shows the multi-pane confirmation dialog when needed and only
    /// then calls <see cref="TabManager.CloseTab"/>. Centralising
    /// here keeps every close path consistent.
    /// </summary>
    public async Task RequestCloseTabAsync(TabModel tab)
        => await TabCloseConfirmation.RequestAsync(_manager, tab, XamlRoot, _dialogs);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvent) return;
        // A live drag holds the strip, and nothing raised while it does is
        // an intent. The engine's crossings commit through the manager as
        // they land, each a remove-then-insert on TabItems, and the
        // selection model's reaction to the remove re-targets the
        // selection -- raising this event while the dragged tab is still
        // settling. Acting on that raise would reconcile mid-crossing.
        // The drag's release is where the selection truth lands instead:
        // the engine's finish re-asserts the manager's active tab after
        // the last commit. No MUXC drag batch can be open under the
        // engine, so the old batch-collision constraint is gone; what
        // remains is that a crossing's churn is not an intent.
        if (_stripDragActive)
        {
            _stoodDownSelectionRaises++;
            TabDragTrace.Line($"COMMIT selection stood-down " +
                $"count={_stoodDownSelectionRaises} item={RowName(TabViewControl.SelectedItem)}");
            return;
        }
        if (TabViewControl.SelectedItem is TabViewItem item)
        {
            // A chip's selection is not an activation. Selecting the chip
            // is the expand gesture: it asks the run to unfold through the
            // same command path every other source uses -- never an
            // Activate, and never onto the chip itself.
            if (item.Tag is TabGroup group)
            {
                // Expanding retires this very chip on this same stack: the
                // command reaches the manager, the group's INPC raise
                // reconciles, and the chip the selection is parked on is
                // removed -- which TabView answers by re-targeting the
                // selection and raising this event again. Unfenced, that
                // re-entry cascade-expands a neighbouring chip or
                // activates whatever it picked. Hold the fence across the
                // command so the nested raises no-op; the SelectActive
                // after it lands the real active tab once the strip has
                // settled. This is the one site that can hold a chip
                // selection, so the fence lives here and not in
                // RemoveGroupChip, whose disarm would cut short the
                // reconcile's own fence window during a rebuild.
                _suppressSelectionEvent = true;
                try
                {
                    _router.RequestCollapseGroup(group, collapsed: false);
                }
                finally
                {
                    _suppressSelectionEvent = false;
                }
                SelectActive();
                return;
            }
            foreach (var (model, vi) in _itemByModel)
            {
                if (vi == item) { _manager.Activate(model); return; }
            }
        }
    }

    private void OnTabViewContextRequested(
        UIElement sender, ContextRequestedEventArgs e)
    {
        // If the right-click landed on a TabViewItem, the per-item
        // ContextFlyout from TabContextMenuBuilder handles it. Bail out.
        var source = e.OriginalSource as DependencyObject;
        if (VisualTreeHelperEx.FindAncestor<TabViewItem>(source) is not null)
            return;

        var flyout = StripContextMenuBuilder.Build(
            _manager, _router, isVertical: false);

        var anchor = (FrameworkElement)sender;
        if (e.TryGetPosition(anchor, out Point position))
        {
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        }
        else
        {
            // Keyboard-triggered (Shift+F10 or context menu key).
            // Show at the sender so keyboard users get a usable anchor.
            flyout.ShowAt(anchor);
        }
        e.Handled = true;
    }

    /// <summary>
    /// True while the drag engine has the strip. The seam cover derives
    /// from the active item's arranged slot, and during a drag that slot
    /// is the engine's crossing preview rather than the manager's order.
    /// </summary>
    private bool _stripDragActive;

    // The selection raises a live drag's stand-down has swallowed, since
    // the last drag began. Every one is a commit's mid-drag churn, and
    // the count says how much churn a single drop actually produces.
    private int _stoodDownSelectionRaises;

    // The release point the drop-at-a-run fork reads, with the flag that
    // says the point is this drag's own. The engine records a fresh
    // point at every live release before the fork can run, so the flag
    // is never seen false today; it stays as DropPointIn's shape guard,
    // whose refusal (no honest point, no join, no side) is the contract
    // any future caller of the fork inherits.
    private Windows.Foundation.Point _lastDropPosition;
    private bool _dropPositionValid;

    // The one live horizontal drag, zero or one. A press over the strip
    // arms a session that stays a click until the pointer travels past
    // the start threshold; the engine never captures the pointer, so a
    // session is ended by its release, a cancel, or the next press after
    // a release the window never saw.
    private HorizontalDragSession? _horizontalDrag;

    private sealed class HorizontalDragSession
    {
        public required TabDragReorder Machine;

        // Exactly one of the two names what the drag carries: a tab for
        // a plain drag, a group for a chip drag. The chip's machine
        // speaks unit space (one slot per run, TabGroupDragUnits'), the
        // plain tab's speaks DragSlots row space.
        public TabModel? Tab;
        public TabGroup? Group;

        public required TabViewItem Item;

        // Press position and the dragged item's arranged center at the
        // press, both in TabViewControl space, so the machine's dragged
        // center rides the pointer without a live follow: the row moves
        // at crossings, and the pointer's travel supplies the intent.
        public required double PressX;
        public required double GrabCenterX;

        // The last pointer X the engine saw, in the same space PressX is
        // in. The join dwell's timer needs it: the ring advances between
        // pointer events, and the dwell's jitter rule is about where the
        // pointer IS, not where it was pressed.
        public double LastX;
    }

    /// <summary>
    /// The horizontal strip's drag engine: the shared capture-less
    /// machine fed by pointer handlers on the host's own root, with
    /// handledEventsToo so a press over any part of an item still arms.
    /// Nothing here sets Handled and nothing captures: presses route by
    /// hit-testing exactly as they did before the engine existed, which
    /// is why a click survives it untouched -- the item's own pipeline
    /// answers a sub-threshold press-release, and the flag the selection
    /// stand-down reads never goes up for one.
    /// </summary>
    private void HookStripDragInput()
    {
        HostRoot.AddHandler(PointerPressedEvent,
            new PointerEventHandler(OnStripPointerPressed), true);
        HostRoot.AddHandler(PointerMovedEvent,
            new PointerEventHandler(OnStripPointerMoved), true);
        HostRoot.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(OnStripPointerReleased), true);
        HostRoot.AddHandler(PointerCanceledEvent,
            new PointerEventHandler(OnStripPointerCanceled), true);
        HostRoot.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler(OnStripPointerCaptureLost), true);
    }

    private void OnStripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_horizontalDrag is not null)
        {
            // A button that came up off the window is a release the host
            // never saw; the stale session ends here rather than blocking
            // every drag after it.
            CancelHorizontalDrag("stale");
        }
        if (!e.GetCurrentPoint(TabViewControl).Properties.IsLeftButtonPressed) return;

        var source = e.OriginalSource as DependencyObject;
        // The item's own controls keep their gestures: a press on the
        // close button or any other button descendant is that button's,
        // never the drag's.
        if (VisualTreeHelperEx.FindAncestor<ButtonBase>(source) is not null) return;
        if (VisualTreeHelperEx.FindAncestor<TabViewItem>(source) is not { } item) return;

        var pressX = e.GetCurrentPoint(TabViewControl).Position.X;
        var drag = item.Tag is TabGroup group
            ? ArmChipDrag(group, item, pressX)
            : ArmTabDrag(item, pressX);
        if (drag is null) return;
        _horizontalDrag = drag;
    }

    /// <summary>
    /// Arm a plain tab drag: row space, the machine seeded at the tab's
    /// slot. Centers are measured once, at the arm, and survive commits
    /// on the machine's own invariant: rows occupy slots in manager
    /// order, so a committed crossing leaves them describing the layout
    /// the strip is arranging toward. They are exact only while slots
    /// stay equal-width -- TabWidthMode="Equal" in TabHost.xaml is a
    /// dependency of this gesture, not a styling choice. A variable-
    /// width mode would need UpdateCenters re-fed per commit, which the
    /// machine already supports.
    /// </summary>
    private HorizontalDragSession? ArmTabDrag(TabViewItem item, double pressX)
    {
        TabModel? dragged = null;
        foreach (var (model, vi) in _itemByModel)
        {
            if (ReferenceEquals(vi, item)) { dragged = model; break; }
        }
        if (dragged is null) return null;

        var (rows, managerIndex) = TabStripProjection.DragSlots(_manager);
        var slot = TabStripProjection.SlotIndexOf(_manager, managerIndex, dragged);
        if (slot < 0 || rows.Count < 2) return null;

        var reps = new FrameworkElement[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            if (!_itemByModel.TryGetValue(rows[i], out var rowItem)) return null;
            reps[i] = rowItem;
        }
        return BuildSession(dragged, null, item, pressX, reps, slot);
    }

    /// <summary>
    /// Arm a chip drag: unit space, one machine slot per body run. The
    /// run is the atom -- the chip can swap past a whole neighbouring
    /// run and never land inside one, because a run landed between
    /// another group's head and its members would split a run the
    /// projector cannot render. The pinned prefix contributes no units
    /// (groups cannot be pinned, and MoveGroup would clamp the crossing
    // right back), so the unit space never offers a crossing the
    /// commit would refuse; the clamp stays as the backstop.
    /// </summary>
    private HorizontalDragSession? ArmChipDrag(TabGroup group, TabViewItem chip, double pressX)
    {
        var units = TabGroupDragUnits.Build(_manager);
        var dragged = -1;
        for (var i = 0; i < units.Count; i++)
        {
            if (ReferenceEquals(units[i].Group, group)) { dragged = i; break; }
        }
        if (dragged < 0 || units.Count < 2) return null;

        var reps = new FrameworkElement[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            var rep = UnitRepresentative(units[i]);
            if (rep is null) return null;
            reps[i] = rep;
        }
        return BuildSession(null, group, chip, pressX, reps, dragged);
    }

    /// <summary>
    /// The element a unit's center is measured from. The minted chip
    /// first: a chip'd collapse renders no member at all -- collapse
    /// does not prune the item map, so the first entry can be the
    /// run's DETACHED head, whose geometry is a refusal, not a center
    /// -- and the chip is the run's visible atom, the honest center.
    /// Else the first member the strip actually renders: an expanded
    /// run, or a collapsed one still holding the active member.
    /// </summary>
    private FrameworkElement? UnitRepresentative(GroupDragUnit unit)
    {
        if (unit.Group is { } group)
        {
            if (_chipByGroup.TryGetValue(group, out var chip)) return chip.Item;
            foreach (var member in _manager.MembersOf(group))
            {
                if (_itemByModel.TryGetValue(member, out var item)
                    && TabViewControl.TabItems.IndexOf(item) >= 0)
                    return item;
            }
            return null;
        }
        return _itemByModel.TryGetValue(unit.Rep, out var lone) ? lone : null;
    }

    /// <summary>
    /// The shared arm tail: measure the representative centers, seed the
    /// machine, and build the session -- or answer null when the strip
    /// cannot measure what the gesture needs. Measurement is the arm's
    /// contract: a NaN center would swallow every crossing past that
    /// slot, so an unmeasurable arm refuses the gesture and the press
    /// stays a click.
    /// </summary>
    private HorizontalDragSession? BuildSession(
        TabModel? tab, TabGroup? group, TabViewItem item, double pressX,
        IReadOnlyList<FrameworkElement> reps, int grabSlot)
    {
        var centers = new double[reps.Count];
        for (var i = 0; i < reps.Count; i++)
        {
            centers[i] = MeasuredCenterX(reps[i]);
            if (double.IsNaN(centers[i])) return null;
        }
        var machine = new TabDragReorder(reps.Count, grabSlot);
        machine.Press(pressX);
        machine.UpdateCenters(centers);
        return new HorizontalDragSession
        {
            Machine = machine,
            Tab = tab,
            Group = group,
            Item = item,
            PressX = pressX,
            GrabCenterX = centers[grabSlot],
        };
    }

    private void OnStripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_horizontalDrag is not { } drag) return;
        var x = e.GetCurrentPoint(TabViewControl).Position.X;
        drag.LastX = x;
        if (drag.Machine.Phase == TabDragPhase.Pressed)
        {
            if (!drag.Machine.Begin(x)) return;
            BeginHorizontalDragVisual(drag);
        }
        drag.Machine.SampleVelocity(x, Environment.TickCount64);
        if (drag.Machine.Phase != TabDragPhase.Dragging) return;

        // The dragged center travels with the pointer from its grab
        // position; the crossings it earns commit live, the strip
        // following the machine rather than waiting for a release.
        var draggedCenter = drag.GrabCenterX + (x - drag.PressX);
        while (drag.Machine.Evaluate(draggedCenter) is { } crossing)
        {
            // A refused crossing rewinds the machine to where the strip
            // really shows the dragged row; the same center would re-earn
            // the same refusal forever, so the tick stops there -- the
            // next pointer move retries with fresh geometry.
            if (!CommitHorizontalCrossing(drag, crossing)) break;
        }
        UpdateJoinDwell(drag, draggedCenter, x);
    }

    // -----------------------------------------------------------------
    // The join dwell: hold with a ring, the horizontal half. The rules
    // are the vertical strip's, down to the tokens and the Core mapping
    // -- a tab held over its neighbour until a ring fills, released into
    // a group -- because a gesture that meant one thing in a sidebar and
    // another in a tab strip would be two gestures wearing one name.
    //
    // What differs is only what a strip can measure: the axis is X, the
    // centers are the drag machine's own (equal-width slots, measured
    // once at the arm), and the ring rides JoinOverlay rather than the
    // vertical's PreviewHost. The crossing engine is untouched here too:
    // the dwell restarts on pointer travel, so it only ever completes
    // over a pointer that has stopped, and a stopped pointer earns no
    // further crossings.
    // -----------------------------------------------------------------

    private readonly TabJoinDwell _joinDwell = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _joinTimer;
    private TabJoinRing? _joinRing;

    /// <summary>
    /// Re-derive the ring from this move's truth. A chip drag rings
    /// nothing -- it carries a whole run, and a run landing inside a run
    /// is a different op -- and neither does a pinned row, since the
    /// prefix outranks membership.
    /// </summary>
    private void UpdateJoinDwell(HorizontalDragSession drag, double draggedCenter, double x)
    {
        if (drag.Group is not null || drag.Tab is not { } dragged || dragged.IsPinned)
        {
            ClearJoinDwell();
            return;
        }

        var (rows, _) = TabStripProjection.DragSlots(_manager);
        var machine = drag.Machine;
        // The machine's slots ARE the projection's rows (ArmTabDrag seeds
        // them together) and stay paired across commits. A count that has
        // drifted means the strip changed under the gesture -- a tab
        // closed -- and a ring drawn off the stale pairing would name the
        // wrong row.
        if (rows.Count != machine.RowCount) { ClearJoinDwell(); return; }
        var centers = new double[machine.RowCount];
        for (var i = 0; i < centers.Length; i++) centers[i] = machine.CenterOf(i);

        var pick = TabJoinDrop.PickTarget(
            centers, machine.Index, draggedCenter, TabStripMotion.JoinBandFraction);
        // The ring never promises a join the release would refuse.
        if (pick < 0 || !TabJoinDrop.CanJoin(_manager, dragged, rows[pick]))
        {
            ClearJoinDwell();
            return;
        }

        _joinDwell.Hold(rows[pick], x, Environment.TickCount64);
        StartJoinTimer();
        UpdateJoinRing();
    }

    /// <summary>
    /// The timer's own pass: advance the ring over the target the last
    /// move picked, without re-measuring. The pointer has not moved --
    /// that is the premise of a dwell -- so the answer cannot have
    /// changed.
    /// </summary>
    private void TickJoinDwell()
    {
        if (_horizontalDrag is not { } drag || _joinDwell.Target is not TabModel target)
        {
            ClearJoinDwell();
            return;
        }
        // Re-asked every tick, not only on the pointer path -- see the vertical
        // strip's copy. The 450ms the dwell measures is precisely the window in
        // which no pointer event arrives, so without this the eligibility rule
        // is not enforced at all while the gesture is being decided.
        if (!TabJoinDrop.CanJoin(_manager, drag.Tab, target))
        {
            ClearJoinDwell();
            return;
        }
        _joinDwell.Hold(target, drag.LastX, Environment.TickCount64);
        UpdateJoinRing();
    }

    /// <summary>
    /// Draw the ring over the target's arranged tab. An unreadable
    /// measurement withdraws it: a ring on a stale rect promises the
    /// wrong row, and no ring is the honest picture.
    /// </summary>
    private void UpdateJoinRing()
    {
        if (_joinDwell.Target is not TabModel target
            || !_itemByModel.TryGetValue(target, out var item)
            || item.ActualHeight <= 0)
        {
            ClearJoinDwell();
            return;
        }
        Point origin;
        try
        {
            origin = item.TransformToVisual(HostRoot).TransformPoint(new Point(0, 0));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            ClearJoinDwell();
            return;
        }

        if (_joinRing is null)
        {
            // The accent OPAQUE, and it has its own resolver rather than
            // borrowing one. The strip used to have a pin-boundary brush that
            // handed back the accent at 0xE6 while a drag was live, and since
            // the ring is only ever drawn during a drag it took that alpha:
            // the halo came out at 0.27 here against the vertical strip's
            // 0.30. One gesture, two layouts, one colour. That brush retired
            // with the boundary stroke, but the rule it taught stands -- an
            // alpha policy written for something else moves when that thing
            // moves, for a reason nobody will connect back to here.
            _joinRing = new TabJoinRing(JoinRingBrush());
            JoinOverlay.Children.Add(_joinRing);
        }
        JoinOverlay.Visibility = Visibility.Visible;
        _joinRing.Place(
            new Rect(origin.X, origin.Y, item.ActualWidth, item.ActualHeight),
            _joinDwell.Progress,
            _joinDwell.IsArmed,
            TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast),
            _highContrast);
    }

    /// <summary>
    /// The join ring's ink: the accent at full strength, resolved the way the
    /// vertical strip's own accent is.
    /// </summary>
    private Brush JoinRingBrush()
    {
        if (Application.Current?.Resources.TryGetValue("SystemAccentColor", out var v) == true
            && v is Windows.UI.Color accent)
            return new SolidColorBrush(accent);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF));
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
    /// Take the promise back. Both drag endings funnel here, so an armed
    /// dwell cannot outlive the drag that armed it and be read by the
    /// next release.
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
        JoinOverlay.Children.Remove(_joinRing);
        _joinRing = null;
        JoinOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnStripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_horizontalDrag is not { } drag) return;
        _horizontalDrag = null;
        if (drag.Machine.Phase != TabDragPhase.Dragging)
        {
            // A press that never crossed the threshold was a click, and
            // the item's own pipeline answers this release. The engine
            // never held the pointer, so there is nothing to hand back
            // and no side effect to undo.
            //
            // Except the ring, which is cleared for the same reason
            // CancelHorizontalDrag clears it before its own phase gate: this
            // path nulls the drag, and the invariant is that no path does that
            // and leaves the promise standing.
            ClearJoinDwell();
            drag.Machine.Cancel();
            return;
        }
        FinishHorizontalDrag(drag, e);
    }

    private void OnStripPointerCanceled(object sender, PointerRoutedEventArgs e)
        => CancelHorizontalDrag("canceled");

    private void OnStripPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => CancelHorizontalDrag("capture");

    /// <summary>
    /// The begin bundle, run once at the threshold, never at the press:
    /// everything here changes what a click looks like or suppresses, so
    /// a gesture that is still a click must not run it. The lift is
    /// decoration and stands last, as it always did.
    /// </summary>
    private void BeginHorizontalDragVisual(HorizontalDragSession drag)
    {
        TabDragTrace.Line($"DRAG begin index={drag.Machine.Index} " +
            $"rows={drag.Machine.RowCount} run={(drag.Group is null ? "no" : "yes")} motion=" +
            (TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast) ? "on" : "off"));
        _stripDragActive = true;
        _stoodDownSelectionRaises = 0;
        // The label hides HERE, in the begin pass, as a cut: an 83ms fade
        // would overlap the dragged tab's lift, which is the one overlap
        // the label rule exists to forbid.
        ApplyLabelPhase(_labelRules.DragStarting());
        // Hidden synchronously: the drag is live from this call onward,
        // and the crossings below move slots the manager is asked to
        // agree to as they land.
        SelectedTabSeamChanged?.Invoke(0, 0, null);
        StartLift(drag.Item);
    }

    /// <summary>
    /// One committed crossing: the machine's slot space translated
    /// through the projection into the manager move, the same seam the
    /// old bridge committed through. A slot the projection cannot map is
    /// a chip's -- a chip is a slot but not a model -- and the machine's
    /// index is rewound: crossing a chip is the group rung's grammar,
    /// not a move this rung may guess at.
    /// </summary>
    /// <summary>
    /// One committed crossing. Answers false when the crossing refused --
    /// the manager would not take it, or the read-back caught a clamp --
    /// so the tick stops instead of re-earning the same refusal.
    /// </summary>
    private bool CommitHorizontalCrossing(HorizontalDragSession drag, TabDragCrossing crossing)
        => drag.Group is { } group
            ? CommitGroupCrossing(drag, group, crossing)
            : CommitTabCrossing(drag, crossing);

    /// <summary>
    /// A plain tab's crossing: the machine's row slot translated through
    /// the projection, the pin boundary classified FIRST -- a crossing
    /// over the prefix is a zone change Move alone would clamp away, so
    /// SetPinned relocates the row to the boundary and the Move then
    /// places it at the crossing's slot in the new zone -- and the truth
    /// read back, because Move clamps at the boundary and no-ops on
    /// collapse. A crossing that did not land rewinds the machine to the
    /// slot the strip actually shows the row in and refuses the tick.
    /// </summary>
    private bool CommitTabCrossing(HorizontalDragSession drag, TabDragCrossing crossing)
    {
        var (rows, managerIndex) = TabStripProjection.DragSlots(_manager);
        if (crossing.To < 0 || crossing.To >= managerIndex.Count)
        {
            drag.Machine.UpdateIndex(crossing.From);
            return false;
        }
        var managerTo = managerIndex[crossing.To];
        var from = _manager.IndexOf(drag.Tab!);
        var zone = TabPinBoundary.Classify(
            drag.Tab!.IsPinned, _manager.PinCount, _manager.Tabs.Count, managerTo);
        if (zone.Op != TabPinZoneOp.None)
        {
            bool pin = zone.Op == TabPinZoneOp.Pin;
            // Groups cannot be pinned: a grouped row crossing the
            // boundary leaves its run, the manager's own SetPinned
            // contract. The commit speaks the truth the projection will
            // render, never a tab dragged through the boundary still
            // pretending to belong to a run.
            _manager.SetPinned(drag.Tab, pin);
            TabDragTrace.Line($"DRAG {(pin ? "pin" : "unpin")} {crossing.From}->{crossing.To}");
            from = _manager.IndexOf(drag.Tab);
            if (from < 0) { CancelHorizontalDrag("closed"); return false; }
        }
        TabDragTrace.Line($"DRAG commit {crossing.From}->{crossing.To}");
        TabDragTrace.Line($"COMMIT move old={from} new={managerTo}");
        _manager.Move(from, managerTo);

        // Read the truth back: the pairing is re-walked post-commit, the
        // move having displaced other rows.
        var (_, nowIndex) = TabStripProjection.DragSlots(_manager);
        var actual = _manager.IndexOf(drag.Tab);
        var actualSlot = nowIndex.IndexOf(actual);
        if (actualSlot < 0)
        {
            // The row left the pairing mid-drag -- a chord-collapse of
            // its run, say. The drag's subject is gone; committing
            // further crossings for a row the strip does not show would
            // zombie the gesture on.
            CancelHorizontalDrag("closed");
            return false;
        }
        if (actual != managerTo)
        {
            // Clamped, not vanished: rewind to the slot the strip
            // actually shows and refuse the tick.
            drag.Machine.UpdateIndex(actualSlot);
            TabDragTrace.Line($"DRAG refused {crossing.From}->{crossing.To}");
            return false;
        }
        drag.Machine.UpdateIndex(actualSlot);
        RebindLift(drag.Item);
        return true;
    }

    /// <summary>
    /// A chip's crossing in unit space: the crossing's pivot run maps
    /// through the unit formulas -- the only crossing-to-index mapping
    /// this commit accepts, because a run landed by slot arithmetic
    /// would split the pivot run across its own head. MoveGroup clamps,
    /// so the truth is read back against a fresh unit build: a crossing
    /// that did not land rewinds the machine to the run's actual unit
    /// and refuses the tick. The group stays collapsed through the drag
    /// -- MoveGroup relocates a run and touches no collapse bit -- which
    /// is the contract: a chip drag is a relocation, never an
    /// expansion.
    /// </summary>
    private bool CommitGroupCrossing(
        HorizontalDragSession drag, TabGroup group, TabDragCrossing crossing)
    {
        var units = TabGroupDragUnits.Build(_manager);
        var dragged = -1;
        for (var i = 0; i < units.Count; i++)
        {
            if (ReferenceEquals(units[i].Group, group)) { dragged = i; break; }
        }
        var pivot = crossing.To;
        if (dragged < 0 || pivot < 0 || pivot >= units.Count || pivot == dragged)
        {
            drag.Machine.UpdateIndex(crossing.From);
            return false;
        }
        bool down = pivot > dragged;
        var target = down
            ? TabGroupDragUnits.TargetAfter(units, units[dragged], pivot)
            : TabGroupDragUnits.TargetBefore(units, pivot);
        TabDragTrace.Line($"DRAG commit {crossing.From}->{crossing.To}");
        TabDragTrace.Line($"COMMIT movegroup target={target}");
        _manager.MoveGroup(group, target);

        var nowUnits = TabGroupDragUnits.Build(_manager);
        var now = -1;
        for (var i = 0; i < nowUnits.Count; i++)
        {
            if (ReferenceEquals(nowUnits[i].Group, group)) { now = i; break; }
        }
        if (now < 0)
        {
            // The run left the unit space mid-drag -- dissolved or
            // regrouped by another actor. Same zombie rule as the tab
            // path: the subject is gone, so the gesture ends.
            CancelHorizontalDrag("closed");
            return false;
        }
        if (nowUnits[now].First != target)
        {
            // Clamped, not vanished: rewind to the run's actual unit and
            // refuse the tick.
            drag.Machine.UpdateIndex(now);
            TabDragTrace.Line($"DRAG refused {crossing.From}->{crossing.To}");
            return false;
        }
        drag.Machine.UpdateIndex(now);
        RebindLift(drag.Item);
        return true;
    }

    /// <summary>
    /// The commit churned the dragged tab's slot -- MoveItem's remove and
    /// insert keeps the same element, but whether the composition visual
    /// carries its running scale, center, and shadow sprite across that
    /// is a runtime question the rebind refuses to ask. Re-assert all
    /// three, the way the vertical's RebindFollow does at every crossing.
    /// Set, not re-spring: re-running the lift spring per crossing would
    /// re-bounce the tab all the way across the strip.
    /// </summary>
    private void RebindLift(TabViewItem item)
    {
        if (_lift is not { } lift || !ReferenceEquals(lift.Item, item)) return;
        Visual visual;
        try
        {
            visual = ElementCompositionPreview.GetElementVisual(item);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return;
        }
        // The churned container scales from its own middle, the same
        // center the grab set, or the lift pivots from the corner after
        // the first mid-drag commit.
        visual.CenterPoint = new Vector3(
            (float)item.ActualWidth / 2f, (float)item.ActualHeight / 2f, 0f);
        visual.Scale = new Vector3(
            TabStripMotion.LiftScale, TabStripMotion.LiftScale, 1f);
        ElementCompositionPreview.SetElementChildVisual(item, lift.Shadow);
        if (!ReferenceEquals(lift.Visual, visual)) _lift = lift with { Visual = visual };
    }

    /// <summary>
    /// The release bundle, run once per live drag. The commit is already
    /// in the manager -- every crossing moved it -- so this pass is the
    /// landing: the join fork for a release that came down on a chip,
    /// the unconditional reconcile sweep, the selection landing the
    /// stand-down owes, and the settle last.
    /// </summary>
    private void FinishHorizontalDrag(HorizontalDragSession drag, PointerRoutedEventArgs e)
    {
        var index = drag.Machine.Drop();
        TabDragTrace.Line($"DRAG completed item=TabViewItem drop={index}");
        // The release point the join fork reads, recorded now that it is
        // this drag's own and none earlier's.
        _lastDropPosition = e.GetCurrentPoint(TabViewControl).Position;
        _dropPositionValid = true;
        // HOLD WITH A RING: a release under a COMPLETED ring joins the
        // dragged tab to the one it was held over, and outranks the chip
        // fork below -- the ring never targets a chip, so the two cannot
        // both be earned, and the arm is the more specific claim about
        // what this release meant. The target is read off the dwell
        // rather than re-derived from the drop point: the promise was
        // made to the row the ring was drawn on, whatever the hand did in
        // the last frame.
        var joined = false;
        if (_joinDwell.IsArmed && _joinDwell.Target is TabModel joinTarget
            && drag.Tab is { } joinDragged)
        {
            var group = TabJoinDrop.Join(_manager, joinDragged, joinTarget);
            TabDragTrace.Line(group is null
                ? "COMMIT join refused"
                : $"COMMIT join group={group.Title}");
            joined = group is not null;
        }
        ClearJoinDwell();
        // A release that came down on a chip is the drop-at-a-run fork:
        // ON the chip joins, beside it positions, by the same geometry
        // the old bridge used -- now from a point the engine actually
        // saw.
        if (!joined
            && VisualTreeHelperEx.FindAncestor<TabViewItem>(e.OriginalSource as DependencyObject)
            is { } releasedOn && releasedOn.Tag is TabGroup)
        {
            // The fork resolves the run whose CHIP the release landed on:
            // the dragged tab's own slot is an item slot by definition, so
            // feeding it to the projector names no group and refuses every
            // join before geometry is ever asked.
            var chipSlot = TabViewControl.TabItems.IndexOf(releasedOn);
            TabDragTrace.Line($"COMMIT drop at chip slot={chipSlot}");
            ResolveDropAtChip(drag.Item, chipSlot);
        }
        _stripDragActive = false;
        // The drag is over: the cut demand lifts, and hover may show the
        // label again; every tab's shape is re-derived from its own pin
        // flag, because a gesture can have pinned or unpinned mid-flight
        // and a crossing the manager refused raised nothing at all.
        ApplyLabelPhase(_labelRules.DragEnded());
        ApplyPinZoneChrome();
        // The sweep is unconditional: a crossing the manager refused or
        // clamped raised no TabMoved, so nothing else would run it.
        ReconcileStripOrder();
        QueueBridgeUpdate();
        // The selection landing the stand-down owes: after the commit and
        // the reconcile, once, the strip resting on the manager's active
        // tab, a reorder never having switched one.
        SelectActive();
        // The settle is decoration and stands last.
        SettleLift();
    }

    private void CancelHorizontalDrag(string reason)
    {
        if (_horizontalDrag is not { } drag) return;
        _horizontalDrag = null;
        var wasDragging = drag.Machine.Phase == TabDragPhase.Dragging;
        drag.Machine.Cancel();
        // Before the phase gate: a press that stayed a click can still
        // have left a ring up from an earlier gesture the window never
        // saw the release of, and the dwell must not outlive it.
        ClearJoinDwell();
        if (!wasDragging) return;
        TabDragTrace.Line($"DRAG cancel reason={reason}");
        _stripDragActive = false;
        ApplyLabelPhase(_labelRules.DragEnded());
        ApplyPinZoneChrome();
        ReconcileStripOrder();
        SelectActive();
        SettleLift();
    }

    /// <summary>
    /// Arranged slot centers in TabViewControl space, one per row, with
    /// the dragged row's center answered separately. FINAL arranged
    /// positions only: a center the strip cannot measure yet refuses the
    /// arm rather than feeding the machine a NaN that would swallow every
    /// crossing past that slot.
    /// </summary>
    /// <summary>
    /// A row's arranged center in TabViewControl space, or NaN when there
    /// is no arranged truth: the item is not in the tree yet, or is
    /// leaving it. The arm refuses on NaN -- an unmeasured slot would
    /// swallow every crossing past it.
    /// </summary>
    private double MeasuredCenterX(FrameworkElement item)
    {
        try
        {
            return item.TransformToVisual(TabViewControl)
                .TransformPoint(new Point(0, 0)).X + item.ActualWidth / 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// A tab dropped at a slot the projection names with a chip: the drop
    /// landed at a collapsed run. Geometry splits the fork. ON the chip
    /// joins the run -- and the join is visible, because JoinGroup is the
    /// manager's own auto-expanding join, so the run the tab actually
    /// joined unfolds on the same commit. BESIDE the chip positions the
    /// tab relative to the run's edge without joining: the run's hidden
    /// members are room the landing clears either way.
    ///
    /// The group comes from the projection, never from the strip's item
    /// order: the members are hidden at drop time, so no TabItems index
    /// can say which run took the drop. A slot the projection does not
    /// name, or a drop whose point the engine did not record, refuses
    /// outright and the reconcile after the commit restores the strip.
    /// </summary>
    private void ResolveDropAtChip(TabViewItem dragged, int slot)
    {
        if (TabStripProjection.VisibleGroupAt(_manager, slot) is not { } chipGroup
            || !_chipByGroup.TryGetValue(chipGroup, out var chip))
            return;

        TabModel? dropped = null;
        foreach (var (model, vi) in _itemByModel)
        {
            if (vi == dragged) { dropped = model; break; }
        }
        if (dropped is null) return;

        var bounds = DropPointIn(chip.Item);
        if (bounds is null)
        {
            TabDragTrace.Line("COMMIT drop at chip refused reason=no-geometry");
            return;
        }

        if (bounds.Value.Contains(_lastDropPosition))
        {
            TabDragTrace.Line($"COMMIT join group={chipGroup.Title}");
            _manager.JoinGroup(dropped, chipGroup);
            return;
        }

        var before = _lastDropPosition.X < bounds.Value.X + bounds.Value.Width / 2;
        // Dead-today insurance: under TabWidthMode="Equal" the chip's arranged
        // bounds span its entire slot, so a release that hit-tests to the chip
        // is always inside bounds and the join above always wins. Beside is
        // live only under a variable-width mode or a tighter bounds source;
        // the fork stays because it is manager-state-tested and one XAML
        // attribute away from reachable.
        var beside = before
            ? TabChipDrop.MemberTargetBefore(_manager, chipGroup)
            : TabChipDrop.MemberTargetAfter(_manager, chipGroup);
        var oldIndex = _manager.IndexOf(dropped);
        if (beside >= 0 && oldIndex >= 0 && oldIndex != beside)
        {
            TabDragTrace.Line($"COMMIT move beside old={oldIndex} new={beside}");
            _manager.Move(oldIndex, beside);
        }
        else
        {
            TabDragTrace.Line($"COMMIT move beside refused old={oldIndex} target={beside}");
        }
    }

    /// <summary>
    /// The recorded drop point against <paramref name="target"/>'s
    /// arranged bounds, or null when there is nothing to compare: the
    /// drag released off the strip (no TabStripDrop, no point) or the
    /// bounds read failed. Null is not a join and not a side -- the fork
    /// refuses and the reconcile speaks, because a geometry guess would
    /// join a tab the user parked beside a run.
    /// </summary>
    private Rect? DropPointIn(TabViewItem target)
    {
        if (!_dropPositionValid) return null;
        try
        {
            var origin = target.TransformToVisual(TabViewControl)
                .TransformPoint(new Point(0, 0));
            return new Rect(origin, new Size(target.ActualWidth, target.ActualHeight));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // The item is not in the tree yet, or is leaving it. There is
            // no honest answer without geometry.
            return null;
        }
    }

    // -----------------------------------------------------------------
    // The horizontal lift, and the collapse swap's appear-hand. TabView
    // owns the drag itself -- the reorder preview, the drop, the ghost
    // it hands the OS -- so what lands here is polish on the slot it
    // already animates: the pressed tab's own visual lifts on the grab
    // and settles back on the release, and a shadow carries the depth a
    // scale alone does not. There is no machine velocity to inherit:
    // TabView exposes none, so both springs start at rest -- the
    // programmatic policy the vertical's pin flight runs on.
    // -----------------------------------------------------------------

    // The one live lift, zero or one. A fresh grab supersedes a settle
    // that is still handing back, and every callback that can end the
    // lift checks the field before it runs -- the batch and the guard
    // fire long after the grab that armed them.
    private HorizontalLift? _lift;

    /// <summary>
    /// The renderable parts of one lift, kept so every ending can undo
    /// exactly what the grab did. The visual is the tab's at grab time;
    /// the shadow is the child sprite, anchored to the element, so it
    /// stays reachable no matter what the strip does to slots in
    /// between.
    /// </summary>
    private sealed record HorizontalLift(
        TabViewItem Item,
        Visual Visual,
        SpriteVisual Shadow,
        Microsoft.UI.Dispatching.DispatcherQueueTimer Guard);

    // The chip <-> members swap's appear-hand, armed when a chip retires
    // and consumed by the rebuild that re-enters its members. Cleared at
    // the end of every reconcile pass: a retirement with nothing to
    // re-enter (a run's last member closed) must not fade some later
    // pass's re-adds.
    private bool _swapFadePending;

    private void StartLift(TabViewItem item)
    {
        // The gate is first, and the cut is total: with motion off the
        // grab performs no composition work at all -- no spring, no
        // shadow, no guard -- and the release finds no lift to settle.
        if (!TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast)) return;
        // One lift at a time: a fresh grab yields the settle still
        // handing back.
        FinishLift("superseded");

        Visual visual;
        try
        {
            visual = ElementCompositionPreview.GetElementVisual(item);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // Composition refusing here is a cut, the same refusal
            // family every geometry read in the drag guards.
            return;
        }
        var compositor = visual.Compositor;

        // The shadow rides a child sprite: it composes behind the item's
        // own content, so only the blur's halo extends past the tab. It
        // is sized once, at the grab -- TabView resizes slots mid-drag
        // under Equal width, and a stale halo for the length of one drag
        // beats re-anchoring the element tree mid-gesture.
        var shadow = compositor.CreateSpriteVisual();
        shadow.Size = new Vector2((float)item.ActualWidth, (float)item.ActualHeight);
        var drop = compositor.CreateDropShadow();
        drop.BlurRadius = (float)TabStripMotion.LiftShadowBlurRadiusPx;
        drop.Offset = new Vector3(0f, (float)TabStripMotion.LiftShadowOffsetYPx, 0f);
        shadow.Shadow = drop;

        var lift = new HorizontalLift(
            item, visual, shadow, DispatcherQueue.CreateTimer());
        _lift = lift;

        // State first, decoration second: the field and the child visual
        // land before any animation starts, so every later ending can
        // reach the things it must undo.
        ElementCompositionPreview.SetElementChildVisual(item, shadow);

        // The lift is the grab's own spring, on the vertical's lift
        // tokens: the same growth the rows use, pivoting from the tab's
        // center so it grows where the pointer holds it.
        visual.CenterPoint = new Vector3(
            (float)item.ActualWidth / 2f, (float)item.ActualHeight / 2f, 0f);
        var scale = compositor.CreateSpringVector3Animation();
        scale.DampingRatio = TabStripMotion.LiftDampingRatio;
        scale.Period = TimeSpan.FromMilliseconds(TabStripMotion.LiftPeriodMs);
        scale.FinalValue = new Vector3(
            TabStripMotion.LiftScale, TabStripMotion.LiftScale, 1f);
        visual.StartAnimation("Scale", scale);

        // The shadow breathes with the grab: the same spring family,
        // scalar, so the depth arrives on the clock the height does.
        var shadowIn = compositor.CreateSpringScalarAnimation();
        shadowIn.DampingRatio = TabStripMotion.LiftDampingRatio;
        shadowIn.Period = TimeSpan.FromMilliseconds(TabStripMotion.LiftPeriodMs);
        shadowIn.FinalValue = TabStripMotion.LiftShadowOpacity;
        shadow.StartAnimation("Opacity", shadowIn);

        // The guard is the completion path's backstop, the pin flight's
        // rule: a batch that never fires must not leave the tab lifted
        // under a shadow. One shot, longer than the lift and the
        // handback together; landing through it is identical to landing
        // through a batch.
        lift.Guard.IsRepeating = false;
        lift.Guard.Interval = TimeSpan.FromMilliseconds(
            3 * (TabStripMotion.LiftPeriodMs + TabStripMotion.SettlePeriodMs)
            + TabStripMotion.UnliftFadeMs + 250);
        lift.Guard.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_lift, lift)) return;
            FinishLift("timeout");
        };
        lift.Guard.Start();
    }

    /// <summary>
    /// The release handback. The scale springs down on the drop-settle
    /// tokens -- at rest, since TabView exposes no machine velocity --
    /// and the shadow fades out alongside: the spring is the landing,
    /// and the fade is what keeps the shadow from popping off a tab
    /// that is still visibly settling.
    /// </summary>
    private void SettleLift()
    {
        if (_lift is not { } lift) return;
        // The element is read fresh: a refused or repaired commit can
        // churn the strip, and the settle must drive the visual the tab
        // has now, not the one the grab lifted.
        Visual visual;
        try
        {
            visual = ElementCompositionPreview.GetElementVisual(lift.Item);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            FinishLift("churned");
            return;
        }
        var compositor = visual.Compositor;
        var settle = compositor.CreateSpringVector3Animation();
        settle.DampingRatio = TabStripMotion.SettleDampingRatio;
        settle.Period = TimeSpan.FromMilliseconds(TabStripMotion.SettlePeriodMs);
        settle.FinalValue = new Vector3(1f, 1f, 1f);
        var settling = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        settling.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_lift, lift)) return;
            FinishLift("landed");
        };
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(TabStripMotion.UnliftFadeMs);
        fadeOut.InsertKeyFrame(1f, 0f);
        lift.Shadow.StartAnimation("Opacity", fadeOut);
        visual.StartAnimation("Scale", settle);
        settling.End();
    }

    /// <summary>
    /// The lift's every ending: landing, timeout, a superseding grab, a
    /// churned commit, and the strip's teardown. Stopping the
    /// animations is bookkeeping; the real undo is detaching the child
    /// sprite, which is anchored to the element and so reaches the tab
    /// whatever the strip did to its slots in between.
    /// </summary>
    private void FinishLift(string reason)
    {
        if (_lift is not { } lift) return;
        _lift = null;
        lift.Guard.Stop();
        lift.Visual.StopAnimation("Scale");
        lift.Shadow.StopAnimation("Opacity");
        ElementCompositionPreview.SetElementChildVisual(lift.Item, null);
    }

    /// <summary>
    /// The collapse swap's appear-hand: what the chip &lt;-&gt; members
    /// swap just revealed fades in on the fade token. What the swap
    /// removed does not linger -- TabView has no item exit, and the
    /// removals stay immediate. Gate off is a cut: the element simply
    /// appears, and no animation is built at all.
    /// </summary>
    private void FadeInAppearing(FrameworkElement appearing)
    {
        if (!TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast)) return;
        Visual visual;
        try
        {
            visual = ElementCompositionPreview.GetElementVisual(appearing);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            return;
        }
        var fadeIn = visual.Compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(TabStripMotion.FadeMs);
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => visual.StopAnimation("Opacity");
        visual.StartAnimation("Opacity", fadeIn);
        batch.End();
    }

    /// <summary>
    /// The window animations preference, read at the gesture, never
    /// cached: reading can throw in packaged/sandboxed contexts
    /// (App.xaml.cs notes the same); unreadable is not "off", so the
    /// gate fails open and High Contrast still collapses the motion
    /// through its own pushed flag.
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

    /// <summary>
    /// Apply palette-derived colors to the tab strip.
    /// Called by MainWindow when shell theme changes.
    /// </summary>
    internal void ApplyShellTheme(ShellThemeService theme)
    {
        if (!theme.IsEnabled) return;

        // The active tab is the field: painted the terminal's own ground, so
        // it and the pane below it are one surface with no line between. The
        // accent is the stroke around that field (SetAccentColor), never the
        // field -- filling with it made the active tab a second, brighter
        // chrome surface and the seam cover then drew that accent across the
        // pane's top border.
        //
        // Which makes this path agree with the default one: both take the
        // terminal pair, so the active tab looks the same whatever
        // window-theme says. What the palette still owns here is everything
        // that is chrome -- the inactive ink below, and the strip's own
        // surface through SetChromeFill.
        _selectedTabFillBrush = new SolidColorBrush(theme.ActiveTabFill);

        // TabViewBackground is deliberately not written here. The strip's
        // surface has one owner, SetChromeFill, which the window always calls
        // after this and which knows the frame's material as well as the
        // palette's shade. Painting it from here too is what made the palette
        // path opaque whatever frame-style said.
        //
        // Selected fill is painted on the header panel so preset tab colors
        // can replace the accent per tab.
        TabViewControl.Resources["TabViewItemHeaderBackgroundSelected"] = TransparentHeaderSelected;

        // Toggle theme to force WinUI to re-read background resources.
        TabViewControl.RequestedTheme = ElementTheme.Light;
        TabViewControl.RequestedTheme = _cachedTheme;

        // Paint the Foreground of each tab's existing title TextBlock.
        // The previous implementation set TabViewItem.HeaderTemplate to
        // a programmatic DataTemplate — in WinUI 3 that replaces the
        // custom StackPanel Header entirely, dropping both the 2px
        // progress bar and the tab-color tint, and its `{Binding}`
        // resolved to the TabViewItem DataContext (the TabModel), so
        // every tab rendered its type name "Ghostty.Core.Tabs.TabModel"
        // instead of the title.
        //
        // Inactive tabs sit on the strip rather than on the accent, so the
        // active brush gives them zero contrast. RefreshShellInactiveInk picks
        // their pole against whatever the strip actually is.
        //
        // Calibrate the active title against the field it sits on, which is
        // the terminal's ground -- so the pair is the terminal's own and
        // normally passes straight through. EnsureReadableForeground stays
        // for the pathological fg/bg that does not contrast (#342); it is no
        // longer rescuing an ink chosen for a different surface.
        uint fieldPacked = PackColor(theme.ActiveTabFill);
        uint activePacked = PackColor(theme.ActiveTabInk);
        _shellActiveTextBrush = TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(fieldPacked, activePacked));

        RefreshShellInactiveInk();
        RefreshTabColors();
    }

    /// <summary>
    /// Unselected tabs are muted rather than given a second colour, so the
    /// selected tab is the only one carrying full-strength ink.
    /// </summary>
    private const byte InactiveInkAlpha = 0xB3;

    /// <summary>
    /// Recalibrate the unselected titles' ink, and the ground the preset tab
    /// colours are mixed against, on the surface the text actually lands on.
    ///
    /// Which is the strip's own fill only while there is one. A frosted or
    /// crystal frame leaves the strip bare so the backdrop shows through, and
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
        // Nothing to calibrate off the palette path: there the unselected
        // titles are left on the element theme's own foreground.
        if (_shellActiveTextBrush is null) return;

        _stripBackdropPacked = _chromeFillRgb ?? _chromeGroundPacked;
        _shellInactiveTextBrush = new SolidColorBrush(
            ThemeResolution.PreferLightForegroundAtAlpha(_stripBackdropPacked, InactiveInkAlpha)
                ? Windows.UI.Color.FromArgb(InactiveInkAlpha, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(InactiveInkAlpha, 0x00, 0x00, 0x00));
        RecolorTabText();
        // The field's wash and its terminals are scored against the
        // same ground this just moved, so they re-ask here rather than
        // waiting for whatever pass happens next: SetChromeFill reaches
        // no other painter, and a run left washed for the previous
        // frame is the ink bug with a different surface.
        RefreshGroupFieldWash();
    }

    /// <summary>
    /// Re-derive every field's ground, through the painters that already
    /// own it: the run's members and, when it is folded, its chip. No
    /// second wash site -- a painter of its own here could disagree with
    /// the one every other pass goes through.
    ///
    /// The terminals need the same push and do not get it from those two.
    /// <see cref="RefreshRunRails"/> and <see cref="RefreshChip"/> repaint
    /// header brushes; only <see cref="UpdateGroupFieldTerminals"/> sets a
    /// bar's Fill, and it rides <see cref="UpdateSelectedTabBridge"/>, which
    /// neither SetChromeFill nor SetChromeGround reaches. So a frame-style
    /// flip moved the ground, the members repainted around the new pole, and
    /// the cap and end bar kept a colour lifted against the field they were
    /// scored on a moment ago -- a Graphite bar lifted for a light field is a
    /// dark grey, which lands near 1.5:1 once the field goes dark. That is the
    /// defect this method exists to prevent, arriving through the one painter
    /// it was not calling.
    /// </summary>
    private void RefreshGroupFieldWash()
    {
        foreach (var group in _manager.Groups)
        {
            RefreshRunRails(group);
            RefreshChip(group);
        }
        QueueBridgeUpdate();
    }

    /// <summary>
    /// The backdrop estimate for the surface behind a bare strip, pushed from
    /// the window because the estimate is a blend of the palette and the
    /// desktop and only the window has both.
    ///
    /// Separate from <see cref="SetChromeFill"/> so the strip is told what is
    /// behind it whether or not the frame is painting over it: the two answers
    /// change on different inputs, and folding them into one call would drop
    /// an OS light/dark flip that never moved the fill.
    /// </summary>
    internal void SetChromeGround(uint groundRgb, bool highContrast)
    {
        var groundChanged = _chromeGroundPacked != groundRgb;
        _chromeGroundPacked = groundRgb;
        // The motion gate's High Contrast half rides the same push: it is
        // a window chrome truth composed from the same inputs the
        // separators read, and the strip must not re-derive it.
        _highContrast = highContrast;
        if (!groundChanged) return;

        if (_shellActiveTextBrush is not null)
        {
            RefreshShellInactiveInk();
            return;
        }

        // The palette path gets the same answer by hand, because
        // RefreshShellInactiveInk returns before reaching the assignment
        // there. Without this the strip's ground stays at whatever
        // SetSelectedTabColors last guessed, and the two consumers that read
        // it -- the preset tints and the field's flight -- are both scored
        // against the terminal instead of the strip. The vertical strip has
        // taken its ground from here since the same bug was fixed there.
        _stripBackdropPacked = groundRgb;
        RefreshTabColors();
        // RefreshTabColors walks _itemByModel; chips live in _chipByGroup and
        // are never re-chromed by it. Without this a folded run's chip keeps
        // the old pole's wash through an OS light/dark flip while the expanded
        // runs around it repaint and its own cap and end bar are re-inked --
        // the same defect RefreshGroupFieldWash exists to prevent, on the path
        // that never reaches RefreshShellInactiveInk.
        RefreshGroupFieldWash();
    }

    private uint _chromeGroundPacked = 0x0C0C0C;

    // High Contrast as the window composed it (detector composed through
    // the opt-out, the one read the vertical's gate uses too). Motion
    // input, not ink input: it gates the lift and the swap fade and
    // touches no brush.
    private bool _highContrast;

    private SolidColorBrush? _shellActiveTextBrush;
    private SolidColorBrush? _shellInactiveTextBrush;

    // Default-path (no shell theme) selected-tab background = the terminal
    // background, and the contrast-safe title brush derived from it. Cached so
    // ClearShellTheme can restore a deterministic selected background and
    // RecolorTabText can keep the active title legible on it.
    private SolidColorBrush? _accentBrush;
    private SolidColorBrush? _defaultActiveTextBrush;

    // Recolor every tab title for the current selection.
    //
    // Shell theme on: active titles use the accent-calibrated brush and
    // inactive titles use the muted near-bg brush — without the split,
    // inactive titles inherited the active brush and vanished (#342).
    //
    // Shell theme off (default): only the active tab sits on the terminal
    // background fill (SetSelectedTabColors paints the selected-tab
    // background). Give that one title the contrast-safe brush; leave the
    // others on the inherited theme foreground (white on the default dark,
    // unselected tab background).
    private void RecolorTabText()
    {
        bool shell = _shellActiveTextBrush is not null && _shellInactiveTextBrush is not null;
        foreach (var (model, tb) in _headerTextByModel)
        {
            bool active = ReferenceEquals(model, _manager.ActiveTab);
            _iconRowByModel.TryGetValue(model, out var iconRow);

            if (model.Color != TabColor.None)
            {
                var fg = TabColorForegroundBrush(model, active);
                tb.Foreground = fg;
                if (iconRow is not null)
                    ApplyHeaderRowForeground(iconRow, fg);
                continue;
            }

            if (iconRow is not null)
                ClearHeaderRowForeground(iconRow);

            // The home glyph is a title, not an ornament: it stands where
            // the text would, so it takes the same ink the text takes.
            if (shell)
            {
                var ink = active ? _shellActiveTextBrush! : _shellInactiveTextBrush!;
                tb.Foreground = ink;
                SetHomeGlyphForeground(iconRow, ink);
            }
            else if (active && _defaultActiveTextBrush is not null)
            {
                tb.Foreground = _defaultActiveTextBrush;
                SetHomeGlyphForeground(iconRow, _defaultActiveTextBrush);
            }
            else
            {
                tb.ClearValue(TextBlock.ForegroundProperty);
                SetHomeGlyphForeground(iconRow, null);
            }
        }

        // Chips ride the same ink pass: they never sit on the selected
        // fill (a chip is never the selected item), but they live on the
        // same backdrop and under the same group-colour rule tabs do.
        RecolorChips();
    }

    // Pack/unpack between WinUI's Windows.UI.Color and the 0x00RRGGBB form
    // ThemeResolution works in.
    private static uint PackColor(Windows.UI.Color c) =>
        ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private ElementTheme _cachedTheme = ElementTheme.Default;


    /// <summary>
    /// Remove shell theme overrides so the TabView reverts to
    /// its default theme resources.
    /// </summary>
    internal void ClearShellTheme()
    {
        // The cache has to describe what is installed, and the SetChromeFill
        // that follows is about to replace it. Left stale, that call would
        // decline to write a fill it thinks is already there.
        //
        // TabViewBackground itself is deliberately not removed here: it has
        // one owner, and a clear from a second place is an answer about the
        // frame's material given by code that does not know it.
        _chromeFillRgb = null;
        _shellActiveTextBrush = null;
        _shellInactiveTextBrush = null;

        // The selected-tab background resource is shared with the default
        // (terminal-background) path, so don't just remove it — restore the
        // cached background. Otherwise a config reload (which clears the shell
        // theme after SetSelectedTabColors has run) would drop the background
        // and the active title's contrast decision would be made against the
        // wrong background. Falls back to removal only before
        // SetSelectedTabColors has ever run (first ClearShellTheme at startup).
        if (_accentBrush is not null)
        {
            _selectedTabFillBrush = _accentBrush;
            TabViewControl.Resources["TabViewItemHeaderBackgroundSelected"] =
                TransparentHeaderSelected;
        }
        else
        {
            _selectedTabFillBrush = null;
            TabViewControl.Resources.Remove("TabViewItemHeaderBackgroundSelected");
        }

        RefreshTabColors();

        // Toggle theme to force WinUI to re-read the background resources.
        // Foregrounds don't need this — RecolorTabText above is immediate.
        TabViewControl.RequestedTheme = ElementTheme.Light;
        TabViewControl.RequestedTheme = _cachedTheme;
    }

    internal void SetRequestedTheme(ElementTheme theme)
    {
        _cachedTheme = theme;
        RequestedTheme = theme;
    }

    /// <summary>
    /// Paint the strip, or leave it to the window backdrop.
    ///
    /// Bare is the absence of the override rather than a colour, which is why
    /// this takes a nullable: writing a transparent brush would still shadow
    /// whatever the TabView's own theme resource resolves to.
    ///
    /// The one owner of TabViewBackground, palette or not. The window resolves
    /// the shade -- the tab bar's under window-theme=wintty, the desktop's
    /// otherwise -- and the frame decides whether it is painted at all, so a
    /// second writer here could only disagree with one of the two.
    ///
    /// Only the strip's surface. The selected header keeps the terminal's
    /// background in every combination, so the active tab reads as continuous
    /// with the pane below it.
    /// </summary>
    internal void SetChromeFill(uint? fillRgb)
    {
        if (_chromeFillRgb == fillRgb) return;
        _chromeFillRgb = fillRgb;

        // The surface the titles sit on just changed, and on the palette path
        // this call is where that happens: the window resolves the fill after
        // it hands over the palette, so the ink ApplyShellTheme picked is one
        // frame behind until here.
        RefreshShellInactiveInk();

        if (fillRgb is { } rgb)
            TabViewControl.Resources["TabViewBackground"] = TabColorBrush.FromPackedRgb(rgb);
        else
            TabViewControl.Resources.Remove("TabViewBackground");

        // Background resources are only re-read on a theme change; same toggle
        // ApplyShellTheme needs, and the memoisation above is what keeps it off
        // every chrome refresh.
        TabViewControl.RequestedTheme = ElementTheme.Light;
        TabViewControl.RequestedTheme = _cachedTheme;
    }

    private uint? _chromeFillRgb;

    /// <summary>
    /// Set the default-path selected-tab background and active-title colours.
    /// The selected tab is painted with the terminal background so the active
    /// tab visually connects to the pane below it; the active title uses the
    /// terminal foreground (which is by definition readable on that
    /// background). Driven by background-color/foreground from the config.
    /// </summary>
    internal void SetSelectedTabColors(
        Windows.UI.Color background, Windows.UI.Color foreground)
    {
        // Cache the terminal background as the default-path selected-tab fill,
        // and a title colour that stays legible on it. Foreground over
        // background is the terminal's own contrast pair, so it normally
        // passes straight through; EnsureReadableForeground only steps in for
        // a pathological fg/bg that doesn't contrast.
        _accentBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, background.R, background.G, background.B));
        _defaultActiveTextBrush = TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(
                PackColor(background), PackColor(foreground)));

        // Deliberately does NOT touch _stripBackdropPacked. That value is the
        // surface the tabs sit on, and this is the terminal background, which
        // is what the selected tab is filled with -- a different thing. While
        // both wrote it, the winner was whichever ran last, and the order
        // differs between construction and a config reload. SetChromeGround
        // owns it now, on both paths.
        //
        // The vertical strip fixed this first, where the symptom was inactive
        // titles calibrated against the terminal. Here the second consumer is
        // the field: leaving the terminal background in there makes the
        // flight's start equal its target, and Settle answers a no-op move
        // with a cut.

        // When a shell theme is active it owns the selected-tab background
        // and the active title (ApplyShellTheme). Don't fight it here; keep
        // the cache warm for the moment the shell theme is turned off.
        if (_shellActiveTextBrush is not null) return;

        _selectedTabFillBrush = _accentBrush;
        TabViewControl.Resources["TabViewItemHeaderBackgroundSelected"] =
            TransparentHeaderSelected;
        RefreshTabColors();

        // Force re-apply by toggling selection so the TabView picks
        // up the new brush. Suppress the event to avoid side effects.
        if (TabViewControl.SelectedItem is not null)
        {
            _suppressSelectionEvent = true;
            var selected = TabViewControl.SelectedItem;
            TabViewControl.SelectedItem = null;
            TabViewControl.SelectedItem = selected;
            _suppressSelectionEvent = false;
        }
    }

}

internal static partial class TabHostLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.TabStrip.ReconcileFailed,
                   Level = LogLevel.Error,
                   Message = "[tabs] strip order reconcile failed; rebuilt the strip from the tab manager")]
    internal static partial void LogReconcileFailed(
        this ILogger logger, System.Exception ex);
}
