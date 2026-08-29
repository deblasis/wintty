using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Input;
using Ghostty.Panes;
using Ghostty.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _suppressSelectionEvent;
    // From the process-wide factory rather than injected: this control is
    // built before DI contexts exist and logs one terminal condition only.
    private readonly ILogger _log =
        (ILogger?)App.LoggerFactory?.CreateLogger<TabHost>() ?? NullLogger.Instance;

    public FrameworkElement HostElement => this;

    public FrameworkElement? TabElement(TabModel tab)
        => _itemByModel.TryGetValue(tab, out var item) ? item : null;

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

        _manager.TabAdded += (_, t) => { AddItem(t); SelectActive(); QueueBridgeUpdate(); };
        _manager.TabRemoved += (_, t) => { RemoveItem(t); QueueBridgeUpdate(); };
        _manager.TabMoved += (_, e) => { MoveItem(e.tab, e.to); QueueBridgeUpdate(); };
        _manager.ActiveTabChanged += (_, _) => { SelectActive(); QueueBridgeUpdate(); };

        // Every one of the calls above can run before the strip has arranged,
        // and the bridge is placed from the selected item's layout slot, so
        // it needs a pass once bounds exist. Width changes move every tab
        // under Equal sizing, so the strip resizing moves it too.
        Loaded += (_, _) => QueueBridgeUpdate();
        TabViewControl.SizeChanged += (_, _) => UpdateSelectedTabBridge();

        // The drag lifecycle gates the seam cover below: mid-drag the
        // strip's slots are TabView's reorder preview, not the manager's
        // order, so the cover (placed from the active item's slot) is
        // suppressed for the drag and re-placed at the drop.
        TabViewControl.TabDragStarting += OnTabDragStarting;

        // TabView.TabItemsChanged stays unwired on purpose. It fires for
        // this control's own writes as much as for TabView's, so a
        // validation handler on it would re-enter the reconcile that
        // mutates TabItems. The validation points are the reconciles in
        // MoveItem and OnTabDragCompleted; both read the manager.
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
        var headerText = new TextBlock { Text = tab.EffectiveTitle };
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
        var iconHost = new TabIconPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        iconHost.Attach(tab.TabIcon);
        iconRow.Children.Add(iconHost);
        iconRow.Children.Add(headerText);

        // Bell indicator: a Ringer glyph shown after the title while the
        // tab has an unacknowledged bell (bell-features `title`). Collapsed
        // by default; toggled from TabModel.BellRinging below.
        var bellGlyph = new FontIcon
        {
            Glyph = "\uEA8F", // Segoe Fluent / MDL2 "Ringer"
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

        headerPanel.Children.Add(iconRow);
        headerPanel.Children.Add(headerBar);

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
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabModel.EffectiveTitle) ||
                e.PropertyName == nameof(TabModel.ShellReportedTitle) ||
                e.PropertyName == nameof(TabModel.UserOverrideTitle))
            {
                headerText.Text = tab.EffectiveTitle;
                ApplyItemAccessibleText(item, tab);
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
                ApplyItemAccessibleText(item, tab);
            }
            else if (e.PropertyName == nameof(TabModel.IsPinned))
            {
                // "Pinned" rides ItemStatus, and the flag change is the
                // only event that always carries it: SetPinned relocates
                // to the zone boundary and skips TabMoved when from == to
                // (the boundary tab pinning up, the last pinned tab
                // unpinning), so a relocation-path refresh alone would
                // leave the boundary tab's status stale for life.
                ApplyItemAccessibleText(item, tab);
            }
        };
        _itemByModel[tab] = item;
        _headerTextByModel[tab] = headerText;
        TabViewControl.TabItems.Add(item);
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
        // PaneHost detach from the shared container is MainWindow's job.
    }

    private void MoveItem(TabModel tab, int to)
    {
        if (!_itemByModel.TryGetValue(tab, out var item)) return;
        var current = TabViewControl.TabItems.IndexOf(item);
        // A drag drop raises this event after TabView has applied the
        // move itself, so the common case is in-place, and re-inserting
        // at the index the item already occupies churns the strip for
        // nothing. But "in-place" is measured against the moved item only
        // -- Normalize can have repaired the rest of the strip around it
        // (group re-gather), so the early return still runs the reconcile.
        if (current == to)
        {
            ReconcileStripOrder();
            return;
        }
        TabViewControl.TabItems.Remove(item);
        TabViewControl.TabItems.Insert(to, item);
        // _paneHostContainer order does not matter — Visibility picks
        // the active one. No reorder needed there.

        // The event's indices are the raw op's, and Normalize may have
        // repaired further than the op asked; the reconcile re-derives
        // the strip from the manager's state and owns the last word.
        ReconcileStripOrder();
    }

    /// <summary>
    /// Bring TabItems back into the order the manager holds, via
    /// <see cref="TabStripProjection"/>. The repair for every seam where
    /// the two can disagree: Normalize's silent relocations after a raw
    /// move, and TabView's own reorder that the manager refused or
    /// clamped. Zero ops when they already agree, so the calls on the
    /// happy paths cost one comparison per tab.
    /// </summary>
    private void ReconcileStripOrder()
    {
        var repaired = false;
        // ListView drops its selection when the selected item is removed
        // and does not restore it on re-insert; live, that drop surfaces
        // as an activation of whatever TabView picked instead. A repair
        // must never read as a tab switch.
        _suppressSelectionEvent = true;
        try
        {
            foreach (var op in TabStripProjection.Diff(
                TabStripProjection.Rows(_manager), StripOrder()))
            {
                var item = _itemByModel[op.Tab];
                TabViewControl.TabItems.Remove(item);
                TabViewControl.TabItems.Insert(op.To, item);
                repaired = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            // Diff throws on membership skew, the lookup on a model
            // without an item; neither is producible today. The pure
            // layer keeps its throws as the oracle; here a terminal's
            // strip must not die, so rebuild from the manager.
            _log.LogReconcileFailed(ex);
            RebuildStripFromManager();
            repaired = true;
        }
        finally
        {
            _suppressSelectionEvent = false;
        }
        if (repaired) SelectActive();
    }

    /// <summary>
    /// The reconcile's last resort: rebuild TabItems in manager order
    /// from the items this host owns, repairing skew in both directions
    /// (a tab the strip lost, an item the manager does not hold).
    /// </summary>
    private void RebuildStripFromManager()
    {
        TabViewControl.TabItems.Clear();
        foreach (var tab in TabStripProjection.Rows(_manager))
            if (_itemByModel.TryGetValue(tab, out var item))
                TabViewControl.TabItems.Add(item);
    }

    /// <summary>
    /// The strip's current order, as models. AddItem put the TabModel in
    /// each item's DataContext, so the order reads back without a second
    /// map to keep in step.
    /// </summary>
    private List<TabModel> StripOrder()
    {
        var order = new List<TabModel>(TabViewControl.TabItems.Count);
        foreach (var item in TabViewControl.TabItems)
            if (item is TabViewItem { DataContext: TabModel tab })
                order.Add(tab);
        return order;
    }

    private void SelectActive()
    {
        // Active-tab PaneHost visibility is owned by MainWindow's
        // shared container. This method only syncs the TabView strip
        // selection.
        if (!_itemByModel.TryGetValue(_manager.ActiveTab, out var item)) return;

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
    }

    private static readonly SolidColorBrush TransparentHeaderSelected =
        new(Microsoft.UI.Colors.Transparent);

    private SolidColorBrush? _selectedTabFillBrush;
    private uint _stripBackdropPacked = 0x0C0C0C;

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
    internal void RefreshSeam()
    {
        QueueBridgeUpdate();
        ArmBridgeRetry();
    }

    private void UpdateSelectedTabBridge()
    {
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
            || _selectedTabFillBrush is null)
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

        var fill = active.Color != TabColor.None
            ? TabColorBrush.From(TabColorPalette.Background(active.Color, selected: true))
            : _selectedTabFillBrush;

        SelectedTabSeamChanged?.Invoke(left, width, fill);
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

        if (tab.Color != TabColor.None)
        {
            normalHandle = TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: false));
            selectedHandle = TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: true));
        }
        else if (selected && _selectedTabFillBrush is not null)
        {
            selectedHandle = _selectedTabFillBrush;
        }

        ApplyTabViewItemHeaderBrushes(viewItem, normalHandle, selectedHandle);

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

        headerPanel.Background = TransparentHeaderSelected;
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
        if (TabViewControl.SelectedItem is TabViewItem item)
        {
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
    /// True while a TabView drag has the strip. The seam cover derives
    /// from the active item's arranged slot, and during a drag that slot
    /// is TabView's reorder preview rather than the manager's order.
    /// </summary>
    private bool _stripDragActive;

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        _stripDragActive = true;
        // Hidden synchronously: the drag is live from this call onward,
        // and anything the strip does from here moves slots the manager
        // has not agreed to yet.
        SelectedTabSeamChanged?.Invoke(0, 0, null);
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        _stripDragActive = false;
        if (args.Item is TabViewItem item)
        {
            var newIndex = TabViewControl.TabItems.IndexOf(item);
            foreach (var (model, vi) in _itemByModel)
            {
                if (vi == item)
                {
                    var oldIndex = _manager.IndexOf(model);
                    if (oldIndex != newIndex && oldIndex >= 0)
                        _manager.Move(oldIndex, newIndex);
                    break;
                }
            }
        }
        // The drop is where the two orders can end up disagreeing with
        // nobody left to fix it: TabView has already applied whatever
        // reorder it wanted, and the manager may have refused the move
        // outright (an invariant clamp mutates nothing and raises
        // nothing) or repaired further than the op asked. The strip
        // yields to the manager; on an accepted drag this is a no-op
        // scan.
        ReconcileStripOrder();
        QueueBridgeUpdate();
    }

    /// <summary>
    /// Apply palette-derived colors to the tab strip.
    /// Called by MainWindow when shell theme changes.
    /// </summary>
    internal void ApplyShellTheme(ShellThemeService theme)
    {
        if (!theme.IsEnabled) return;

        var accentBrush = new SolidColorBrush(theme.AccentColor);
        _selectedTabFillBrush = accentBrush;

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
        // Calibrate the active title against the accent it sits on. The raw
        // ActiveTabText (cursor-text, or the bg fallback) can land at the
        // same luminance pole as the accent for some palettes (both light or
        // both dark), which erases the title. Keep it when it contrasts;
        // otherwise drop to a readable black/white. (#342)
        uint accentPacked = PackColor(theme.AccentColor);
        uint activePacked = PackColor(theme.ActiveTabText);
        _shellActiveTextBrush = TabColorBrush.FromPackedRgb(
            ThemeResolution.EnsureReadableForeground(accentPacked, activePacked));

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
    internal void SetChromeGround(uint groundRgb)
    {
        if (_chromeGroundPacked == groundRgb) return;
        _chromeGroundPacked = groundRgb;
        RefreshShellInactiveInk();
    }

    private uint _chromeGroundPacked = 0x0C0C0C;

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
            _itemByModel.TryGetValue(model, out var viewItem);
            var iconRow = viewItem?.Header is StackPanel headerPanel
                && headerPanel.Children.Count > 0
                && headerPanel.Children[0] is StackPanel row
                ? row
                : null;

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

            if (shell)
                tb.Foreground = active ? _shellActiveTextBrush! : _shellInactiveTextBrush!;
            else if (active && _defaultActiveTextBrush is not null)
                tb.Foreground = _defaultActiveTextBrush;
            else
                tb.ClearValue(TextBlock.ForegroundProperty);
        }
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

        // Preset tint foregrounds blend against the tab-bar backdrop, which
        // ApplyShellTheme sets from TabBarBackground -- not terminal bg.
        if (_shellActiveTextBrush is null)
            _stripBackdropPacked = PackColor(background);

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
