using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Ghostty.Core.Config;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Panes;
using Ghostty.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Ghostty.Tabs;

/// <summary>
/// Vertical-sidebar tab host. Sibling of <see cref="TabHost"/>.
/// Two-column Grid: <see cref="VerticalTabStrip"/> in column 0,
/// active tab's <c>PaneHost</c> in column 1. All pane hosts live
/// as siblings in <c>PaneHostContainer</c> with Visibility toggled
/// for the active one — same SwapChainPanel-safety pattern as
/// <see cref="TabHost"/>.
///
/// Collapsed-only in this commit. Animation, expanded layout,
/// drag handle, and hover-expand come in later commits.
/// </summary>
internal sealed partial class VerticalTabHost : UserControl, ITabHost
{
    private readonly TabManager _manager;
    private readonly PaneActionRouter _router;
    private readonly DialogTracker _dialogs;
    private readonly GhosttyHost? _host;
    private readonly VerticalTabStrip _strip;
    private readonly ColumnDragHandle _dragHandle;

    /// <summary>
    /// The wintty icon pinned at the top of the vertical strip. Exposed
    /// so the layout switch can spin it independently of the chrome.
    /// </summary>
    public FrameworkElement IconBadge => IconBadgeHost;

    private double _expandedWidth = WindowsOnlyKeyParsers.VerticalTabsWidthDefault;
    private bool _pinnedExpanded;
    private bool _hoverExpandEnabled;
    private bool _hoverHooksAttached;
    private bool _pointerOverStrip;
    private const int HoverEnterDelayMs = 200;
    private const int HoverLeaveDelayMs = 400;
    private const int TypingSuppressionMs = 1500;

    private VerticalTabStripState _state = VerticalTabStripState.Collapsed;
    private DispatcherQueueTimer? _hoverEnterTimer;
    private DispatcherQueueTimer? _hoverLeaveTimer;

    public FrameworkElement HostElement => this;

    // Drag region lives in MainWindow now (see #171). MainWindow
    // passes its own title-bar grid to SetTitleBar in vertical mode.
    public UIElement DragRegion => this;

    /// <summary>
    /// Raised when the chevron or Ctrl+Shift+Space flips the pinned
    /// state. MainWindow owns the outer strip column width (via
    /// RootGrid.ColumnDefinitions[0]) and animates it in response.
    /// </summary>
    public event EventHandler<double>? StripWidthChangeRequested;

    /// <summary>
    /// Outer strip column target for the current pin state. LayoutCoordinator
    /// Snap/Animate use this so a pinned-expanded config does not get
    /// smashed back to the icon-rail width on every layout snap.
    /// </summary>
    internal double CurrentStripTarget =>
        _pinnedExpanded
            ? _expandedWidth
            : Ghostty.Shell.LayoutCoordinator.VerticalStripCollapsedWidth;

    public VerticalTabHost(TabManager manager, PaneActionRouter router, DialogTracker dialogs, GhosttyHost? host = null)
    {
        InitializeComponent();
        _manager = manager;
        _router = router;
        _dialogs = dialogs;
        _host = host;

        _strip = new VerticalTabStrip(manager);
        _strip.CloseRequestedFromRow += async tab => await RequestCloseTabAsync(tab);
        StripHost.Content = _strip;

        // Drag handle for live resize in pinned-expanded mode.
        // Hidden by default; TogglePinned shows it when entering
        // the pinned state and hides it on collapse.
        _dragHandle = new ColumnDragHandle(
            onWidthChanged: w =>
            {
                StripColumn.Width = new GridLength(w);
                StripWidthChangeRequested?.Invoke(this, w);
            },
            readCurrentWidth: () => StripColumn.Width.Value)
        {
            Visibility = Visibility.Collapsed,
            Height = double.NaN, // stretch via Canvas parent sizing
        };
        HandleHost.Children.Add(_dragHandle);
        // Bind the handle's height to the HandleHost size so it
        // spans the whole strip vertically.
        HandleHost.SizeChanged += (_, e) => _dragHandle.Height = e.NewSize.Height;

        ApplyFromConfig(App.ConfigService);
        if (App.ConfigService is { } cfg)
        {
            cfg.ConfigChanged += OnConfigChanged;
            Unloaded += (_, _) => cfg.ConfigChanged -= OnConfigChanged;
        }

        // The new-tab button is the composite NewTabSplitButton;
        // it routes Click / Alt+Click / Shift+Click through
        // MainWindow.OpenProfile after MainWindow calls AttachOwner.
        // The chevron is still strip-local (toggles pinned state).
        _strip.ChevronToggled += (_, _) => TogglePinned();
    }

    /// <summary>
    /// Toggle the pinned-expanded state. Called by the chevron
    /// button click and (in a later commit) by the
    /// Ctrl+Shift+Space keyboard chord.
    /// </summary>
    internal void TogglePinnedFromKeyboard() => TogglePinned();

    /// <summary>
    /// Forward the owning window into the strip's
    /// <see cref="NewTabSplitButton"/> so its click handlers can call
    /// <see cref="MainWindow.OpenProfile"/>. Mirrors
    /// <see cref="TabHost.AttachOwner"/>; <see cref="MainWindow"/>
    /// invokes both immediately after constructing the hosts.
    /// </summary>
    internal void AttachOwner(MainWindow owner) => _strip.AttachOwner(owner);

    private void OnConfigChanged(IConfigService cfg) => ApplyFromConfig(cfg);

    private void ApplyFromConfig(IConfigService? cfg)
    {
        _expandedWidth = cfg?.VerticalTabsWidth
            ?? WindowsOnlyKeyParsers.VerticalTabsWidthDefault;
        var wantPinned = cfg?.VerticalTabsPinned ?? false;
        _hoverExpandEnabled = cfg?.VerticalTabsHoverExpand ?? false;
        SyncHoverHooks();

        // Cold start: honor pinned without firing the tween (LayoutCoordinator
        // has not subscribed yet). Reloads go through ApplyPinned so the
        // outer column actually moves.
        if (StripWidthChangeRequested is null)
        {
            _pinnedExpanded = wantPinned;
            _state = wantPinned
                ? VerticalTabStripState.PinnedExpanded
                : VerticalTabStripState.Collapsed;
            _strip.IsExpanded = _pinnedExpanded;
            _dragHandle.Visibility = _pinnedExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        ApplyPinned(wantPinned);
        if (!_pinnedExpanded
            && _state is VerticalTabStripState.HoverExpanded
                or VerticalTabStripState.HoverExpanding)
        {
            StripHost.Width = _expandedWidth;
        }
    }

    private void SyncHoverHooks()
    {
        if (_hoverExpandEnabled == _hoverHooksAttached) return;
        if (_hoverExpandEnabled)
        {
            // StripHost is a ContentPresenter: pointer hits land on
            // _strip, and PointerEntered does not bubble, so hooking
            // the presenter never fires.
            _strip.PointerEntered += OnStripPointerEntered;
            _strip.PointerExited += OnStripPointerExited;
            _hoverHooksAttached = true;
        }
        else
        {
            _strip.PointerEntered -= OnStripPointerEntered;
            _strip.PointerExited -= OnStripPointerExited;
            _hoverHooksAttached = false;
            _hoverEnterTimer?.Stop();
            _hoverLeaveTimer?.Stop();
            if (_state is VerticalTabStripState.HoverExpanded
                or VerticalTabStripState.HoverExpanding
                or VerticalTabStripState.HoverCollapsing)
            {
                BeginHoverCollapse();
            }
        }
    }

    private void OnSwitchLayoutClick(object sender, RoutedEventArgs e)
        => _router.RequestToggleTabLayout();

    private void TogglePinned() => ApplyPinned(!_pinnedExpanded);

    private void ApplyPinned(bool pinned)
    {
        if (_pinnedExpanded == pinned)
        {
            if (pinned)
                StripWidthChangeRequested?.Invoke(this, _expandedWidth);
            return;
        }

        _pinnedExpanded = pinned;
        _strip.IsExpanded = _pinnedExpanded;
        _dragHandle.Visibility = _pinnedExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (pinned)
        {
            // Pin owns the column width; drop any hover overlay so the
            // two expand modes cannot fight.
            ClearHoverOverlay();
            _state = VerticalTabStripState.PinnedExpanded;
        }
        else
        {
            _state = VerticalTabStripState.Collapsed;
        }
        StripWidthChangeRequested?.Invoke(this, CurrentStripTarget);

        // Chevron-collapse leaves the pointer on the rail, so
        // PointerEntered never fires again. Resume hover from the
        // current pointer position.
        if (!pinned && _hoverExpandEnabled && _pointerOverStrip)
        {
            EnsureHoverEnterTimer();
            _hoverEnterTimer!.Start();
        }
    }

    private void ClearHoverOverlay()
    {
        _hoverEnterTimer?.Stop();
        _hoverLeaveTimer?.Stop();
        StripHost.Width = double.NaN;
        Canvas.SetZIndex(StripHost, 0);
    }

    /// <summary>
    /// Called by MainWindow's tween loop to update our own internal
    /// column so the strip visual fills the outer column that
    /// MainWindow is simultaneously tweening.
    /// </summary>
    internal void SetInternalStripWidth(double width)
    {
        StripColumn.Width = new GridLength(width);
    }

    // AnimateColumnWidth (old in-host tween) was removed in #171.
    // MainWindow now owns the animation so it can drive the RootGrid
    // strip column in lockstep with our internal column.

    // PaneHost parenting/visibility moved to MainWindow in #171. See
    // there for the shared container the two tab hosts both sit on top of.

    // Hover-expand state machine -----------------------------------------

    private void OnStripPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverStrip = true;
        if (_state != VerticalTabStripState.Collapsed) return;
        if (_pinnedExpanded) return;
        if (IsUserCurrentlyTyping()) return;

        EnsureHoverEnterTimer();
        _hoverEnterTimer!.Start();
    }

    private void EnsureHoverEnterTimer()
    {
        if (_hoverEnterTimer is not null) return;
        _hoverEnterTimer = DispatcherQueue.CreateTimer();
        _hoverEnterTimer.Interval = TimeSpan.FromMilliseconds(HoverEnterDelayMs);
        _hoverEnterTimer.IsRepeating = false;
        // Subscribe once: Start/Stop controls firing, not subscription
        // state. Re-subscribing on every PointerEntered would stack
        // handlers because the previous subscription is only removed
        // in OnHoverEnterTick, which never runs on a Stopped timer.
        _hoverEnterTimer.Tick += OnHoverEnterTick;
    }

    private void OnHoverEnterTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_state != VerticalTabStripState.Collapsed) return;
        if (_pinnedExpanded) return;
        BeginHoverExpand();
    }

    private void OnStripPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverStrip = false;
        _hoverEnterTimer?.Stop();
        if (_pinnedExpanded) return;
        if (_state != VerticalTabStripState.HoverExpanded) return;

        EnsureHoverLeaveTimer();
        _hoverLeaveTimer!.Start();
    }

    private void EnsureHoverLeaveTimer()
    {
        if (_hoverLeaveTimer is not null) return;
        _hoverLeaveTimer = DispatcherQueue.CreateTimer();
        _hoverLeaveTimer.Interval = TimeSpan.FromMilliseconds(HoverLeaveDelayMs);
        _hoverLeaveTimer.IsRepeating = false;
        // Subscribe once; see EnsureHoverEnterTimer for the rationale.
        _hoverLeaveTimer.Tick += OnHoverLeaveTick;
    }

    private void OnHoverLeaveTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_state != VerticalTabStripState.HoverExpanded) return;
        BeginHoverCollapse();
    }

    private bool IsUserCurrentlyTyping()
    {
        if (_host is null) return false;
        var elapsed = (DateTime.UtcNow - _host.LastKeystrokeTimestamp).TotalMilliseconds;
        return elapsed < TypingSuppressionMs;
    }

    private void BeginHoverExpand()
    {
        // After #171 the host lives in RootGrid column 0. An in-host
        // overlay (StripHost.Width) is clipped to that column, so hover
        // has to tween the outer strip column. Leave-collapse tweens it
        // back. Not pinned: no drag handle.
        _state = VerticalTabStripState.HoverExpanding;
        _strip.IsExpanded = true;
        StripWidthChangeRequested?.Invoke(this, _expandedWidth);
        _state = VerticalTabStripState.HoverExpanded;
    }

    private void BeginHoverCollapse()
    {
        _state = VerticalTabStripState.HoverCollapsing;
        _strip.IsExpanded = false;
        StripWidthChangeRequested?.Invoke(
            this, Ghostty.Shell.LayoutCoordinator.VerticalStripCollapsedWidth);
        _state = VerticalTabStripState.Collapsed;
    }

    // Active-tab title tracking moved to MainWindow in #171.

    private void OnStripContextRequested(
        UIElement sender, ContextRequestedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = VisualTreeHelperEx.FindAncestor<ListViewItem>(source);
        if (row is not null)
        {
            // Per-tab menu on the row. There is no TabViewItem.ContextFlyout
            // in the vertical strip; swallowing this used to show nothing.
            var tab = row.Content as TabModel ?? row.DataContext as TabModel;
            if (tab is null) return;
            var tabFlyout = TabContextMenuBuilder.Build(
                _manager,
                tab,
                RequestCloseTabAsync,
                requestDetachToNewWindow: t => TabWindowActions.DetachToNewWindow(XamlRoot, t),
                _dialogs,
                toggleTabLayout: () => _router.RequestToggleTabLayout(),
                isVertical: true,
                getSnapSource: () => TabWindowActions.GetSnapSource(XamlRoot),
                detachWithZone: (t, z) => TabWindowActions.DetachWithZone(XamlRoot, t, z));
            var tabAnchor = (FrameworkElement)sender;
            if (e.TryGetPosition(tabAnchor, out Point tabPos))
                tabFlyout.ShowAt(tabAnchor, new FlyoutShowOptions { Position = tabPos });
            else
                tabFlyout.ShowAt(row);
            e.Handled = true;
            return;
        }

        bool collapsed = !_pinnedExpanded; // not pinned → icon rail

        var flyout = StripContextMenuBuilder.Build(
            _manager,
            _router,
            isVertical: true,
            isSidebarCollapsed: collapsed);

        var anchor = (FrameworkElement)sender;
        if (e.TryGetPosition(anchor, out Point position))
        {
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        }
        else
        {
            flyout.ShowAt(anchor);
        }
        e.Handled = true;
    }

    /// <inheritdoc/>
    public async Task RequestCloseTabAsync(TabModel tab)
        => await TabCloseConfirmation.RequestAsync(_manager, tab, XamlRoot, _dialogs);

    /// <summary>
    /// Apply palette-derived colors to the vertical tab strip.
    /// Called by MainWindow when shell theme changes.
    /// </summary>
    internal void ApplyShellTheme(ShellThemeService theme)
    {
        if (!theme.IsEnabled) return;

        var tabBg = Microsoft.UI.ColorHelper.FromArgb(
            theme.TabBarBackground.A, theme.TabBarBackground.R,
            theme.TabBarBackground.G, theme.TabBarBackground.B);

        _strip.Background = new SolidColorBrush(tabBg);
    }

    internal void ClearShellTheme()
    {
        _strip.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    internal void SetRequestedTheme(ElementTheme theme)
    {
        RequestedTheme = theme;
    }

    /// <summary>
    /// Set the accent color for the vertical tab strip's selected
    /// indicator bar. Driven by cursor-color from the terminal config.
    /// </summary>
    internal void SetAccentColor(Windows.UI.Color color)
    {
        // Update the shared StripAccentBrush defined in VerticalTabStrip.xaml.
        // Since all AccentBar rectangles reference this same brush instance
        // via StaticResource, changing its Color updates them all immediately.
        if (_strip.Resources.TryGetValue("StripAccentBrush", out var res)
            && res is SolidColorBrush brush)
        {
            brush.Color = Microsoft.UI.ColorHelper.FromArgb(
                color.A, color.R, color.G, color.B);
        }
    }
}
