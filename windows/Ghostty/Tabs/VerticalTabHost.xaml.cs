using System;
using System.ComponentModel;
using System.Threading.Tasks;
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

    // TODO(config): vertical-tabs-width (int px, default 220)
    private const double ExpandedWidth = 220;
    // TODO(config): vertical-tabs-pinned (bool, default false)
    private bool _pinnedExpanded = false;

    // TODO(config): vertical-tabs-hover-expand (bool, default false)
    private const bool HoverExpandEnabled = false;
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

        // Hover-expand pointer hooks. The state machine is gated
        // behind HoverExpandEnabled so flipping the constant is
        // the only thing needed to test it. The constant makes one
        // branch of the if statically unreachable; suppress CS0162
        // here specifically because the whole point of this gate
        // is to be toggled for manual testing.
#pragma warning disable CS0162
        if (HoverExpandEnabled)
        {
            StripHost.PointerEntered += OnStripPointerEntered;
            StripHost.PointerExited += OnStripPointerExited;
        }
#pragma warning restore CS0162

        // PaneHost parenting and visibility are owned by MainWindow
        // via a shared container (see #171). Same for the title-bar
        // TextBlock that used to live in this UserControl.

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

    private void OnSwitchLayoutClick(object sender, RoutedEventArgs e)
        => _router.RequestToggleTabLayout();

    private void TogglePinned()
    {
        _pinnedExpanded = !_pinnedExpanded;
        _strip.IsExpanded = _pinnedExpanded;
        var target = _pinnedExpanded
            ? ExpandedWidth
            : Ghostty.Shell.LayoutCoordinator.VerticalStripCollapsedWidth;
        // MainWindow listens on this event and tweens both its
        // RootGrid outer strip column AND our internal column in
        // lockstep so the sidebar actually grows on-screen.
        StripWidthChangeRequested?.Invoke(this, target);
        _dragHandle.Visibility = _pinnedExpanded ? Visibility.Visible : Visibility.Collapsed;
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
        BeginHoverExpand();
    }

    private void OnStripPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverEnterTimer?.Stop();
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
        // Render the strip as an overlay: raise its Z-order and
        // give it an explicit Width so it floats over the terminal
        // column. The ColumnDefinition is NOT resized, so the
        // terminal content does not reflow — that's the key
        // difference from pinned-expanded.
        _state = VerticalTabStripState.HoverExpanding;
        _strip.IsExpanded = true;
        Canvas.SetZIndex(StripHost, 100);
        StripHost.Width = ExpandedWidth;
        _state = VerticalTabStripState.HoverExpanded;
    }

    private void BeginHoverCollapse()
    {
        _state = VerticalTabStripState.HoverCollapsing;
        _strip.IsExpanded = false;
        StripHost.Width = double.NaN;
        Canvas.SetZIndex(StripHost, 0);
        _state = VerticalTabStripState.Collapsed;
    }

    // Active-tab title tracking moved to MainWindow in #171.

    private void OnStripContextRequested(
        UIElement sender, ContextRequestedEventArgs e)
    {
        // If the right-click landed on a ListViewItem (a tab), leave it
        // to whatever per-item flyout exists (or to future per-tab menu
        // work). Strip menu is only for empty sidebar space.
        var source = e.OriginalSource as DependencyObject;
        if (VisualTreeHelperEx.FindAncestor<ListViewItem>(source) is not null)
            return;

        // Collapsed == not pinned. If pinned, the sidebar is expanded;
        // if unpinned, it's collapsed.
        bool collapsed = !_pinnedExpanded;

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
    {
        // TODO(config): confirm-close-multi-pane (bool, default true)
        const bool confirmCloseMultiPane = true;

        var paneCount = tab.PaneHost.PaneCount;
        if (confirmCloseMultiPane && paneCount > 1)
        {
            var dlg = new ContentDialog
            {
                Title = "Close tab?",
                Content = $"This tab has {paneCount} panes. Close all of them?",
                PrimaryButtonText = "Close all",
                SecondaryButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = XamlRoot,
            };
            using (_dialogs.Track(dlg))
            {
                var res = await dlg.ShowAsync();
                if (res != ContentDialogResult.Primary) return;
            }
        }
        _manager.CloseTab(tab);
    }

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
