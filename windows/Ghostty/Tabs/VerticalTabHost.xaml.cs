using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Hosting;
using Ghostty.Panes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ghostty.Tabs;

/// <summary>
/// Vertical-sidebar tab host. Sibling of <see cref="TabHost"/>.
/// Two-column Grid: <see cref="VerticalTabStrip"/> in column 0,
/// active tab's <c>PaneHost</c> in column 1. All pane hosts live
/// as siblings in <c>PaneHostContainer</c> with Visibility toggled
/// for the active one — same SwapChainPanel-safety pattern as
/// <see cref="TabHost"/>.
/// </summary>
internal sealed partial class VerticalTabHost : UserControl, ITabHost
{
    private readonly TabManager _manager;
    private readonly GhosttyHost? _host;
    private readonly VerticalTabStrip _strip;
    private readonly ColumnDragHandle _dragHandle;

    // TODO(config): vertical-tabs-width (int px, default 220)
    private const double ExpandedWidth = 220;
    // TODO(config): vertical-tabs-pinned (bool, default false)
    private bool _pinnedExpanded = false;

    // TODO(config): vertical-tabs-hover-expand (bool, default false).
    // static readonly (not const) so the compiler does not fold the
    // gated branches away; this avoids the CS0162 dead-code suppressions
    // the const version needed and keeps both branches reachable for
    // a single-line runtime flip during manual testing.
    private static readonly bool HoverExpandEnabled = false;
    private const int HoverEnterDelayMs = 200;
    private const int HoverLeaveDelayMs = 400;
    private const int TypingSuppressionMs = 1500;

    private VerticalTabStripState _state = VerticalTabStripState.Collapsed;
    private DispatcherQueueTimer? _hoverEnterTimer;
    private DispatcherQueueTimer? _hoverLeaveTimer;

    // Width-tween state. Keeping these as fields (rather than per-call
    // locals) lets a new TogglePinned cancel the in-flight timer so two
    // animations cannot fight over StripColumn.Width.
    private DispatcherQueueTimer? _widthTweenTimer;
    private Stopwatch? _widthTweenClock;
    private double _widthTweenStart;
    private double _widthTweenTarget;

    public FrameworkElement HostElement => this;
    public UIElement DragRegion => CustomDragRegion;

    public VerticalTabHost(TabManager manager, GhosttyHost? host = null)
    {
        InitializeComponent();
        _manager = manager;
        _host = host;

        _strip = new VerticalTabStrip(manager);
        _strip.CloseRequestedFromRow += async tab => await RequestCloseTabAsync(tab);
        StripHost.Content = _strip;

        // Drag handle for live resize in pinned-expanded mode.
        // Hidden by default; TogglePinned shows it on pin, hides on collapse.
        _dragHandle = new ColumnDragHandle(
            onWidthChanged: w => StripColumn.Width = new GridLength(w),
            readCurrentWidth: () => StripColumn.Width.Value)
        {
            Visibility = Visibility.Collapsed,
        };
        HandleHost.Children.Add(_dragHandle);
        // The handle's own VerticalAlignment=Stretch fills the host by
        // default; we update Height on SizeChanged so it tracks the
        // container exactly even across theme/DPI changes.
        HandleHost.SizeChanged += (_, e) => _dragHandle.Height = e.NewSize.Height;

        // Hover-expand pointer hooks. Subscribed unconditionally once
        // at construction and gated at call time by HoverExpandEnabled
        // — that avoids the re-subscription leak that per-event hooking
        // would cause, and keeps both branches reachable.
        StripHost.PointerEntered += OnStripPointerEntered;
        StripHost.PointerExited += OnStripPointerExited;

        foreach (var t in _manager.Tabs) AddPane(t);
        SwapActivePane();
        RebindActiveTitle();

        _manager.TabAdded += (_, t) => { AddPane(t); SwapActivePane(); };
        _manager.TabRemoved += (_, t) => RemovePane(t);
        _manager.ActiveTabChanged += (_, _) => { SwapActivePane(); RebindActiveTitle(); };

        _strip.NewTabRequested += (_, _) => _manager.NewTab();
        _strip.ChevronToggled += (_, _) => TogglePinned();
    }

    /// <summary>
    /// Toggle the pinned-expanded state. Called by the chevron
    /// button and by the Ctrl+Shift+Space keyboard chord.
    /// </summary>
    internal void TogglePinnedFromKeyboard() => TogglePinned();

    private void TogglePinned()
    {
        _pinnedExpanded = !_pinnedExpanded;
        _strip.IsExpanded = _pinnedExpanded;
        AnimateColumnWidth(_pinnedExpanded ? ExpandedWidth : 40);
        _dragHandle.Visibility = _pinnedExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateColumnWidth(double target)
    {
        // WinUI 3's Storyboard + GridLengthProxy combination does not
        // resolve against a field-held DependencyObject, so we drive
        // the tween manually on the dispatcher queue.
        //
        // Stopwatch instead of DateTime.UtcNow: DateTime has ~15 ms
        // resolution on Windows and jumps on NTP sync / DST changes,
        // both of which can stall or rewind the tween. Stopwatch uses
        // QueryPerformanceCounter and is monotonic.
        //
        // Cancel any in-flight tween before starting a new one —
        // otherwise a rapid chord-toggle would leave two timers
        // fighting over StripColumn.Width.
        _widthTweenTimer?.Stop();

        _widthTweenStart = StripColumn.Width.Value;
        _widthTweenTarget = target;
        _widthTweenClock = Stopwatch.StartNew();

        var duration = TimeSpan.FromMilliseconds(150);
        _widthTweenTimer ??= DispatcherQueue.CreateTimer();
        _widthTweenTimer.Interval = TimeSpan.FromMilliseconds(16);
        _widthTweenTimer.IsRepeating = true;
        // Subscribe once; a field-held timer means the handler must
        // not re-add itself per Start(). Guarded via _widthTweenClock
        // so a canceled tween stops cleanly.
        _widthTweenTimer.Tick -= OnWidthTweenTick;
        _widthTweenTimer.Tick += OnWidthTweenTick;
        _widthTweenTimer.Start();

        void OnWidthTweenTick(DispatcherQueueTimer sender, object args)
        {
            var clock = _widthTweenClock;
            if (clock is null) { sender.Stop(); return; }

            var progress = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            // Ease-out quadratic: 1 - (1 - p)^2
            var eased = 1 - (1 - progress) * (1 - progress);
            var current = _widthTweenStart + (_widthTweenTarget - _widthTweenStart) * eased;
            StripColumn.Width = new GridLength(current);

            if (progress >= 1.0)
            {
                sender.Stop();
                _widthTweenClock = null;
                // Reflow the terminal once at the final size rather
                // than on every tick. libghostty's resize is driven by
                // LayoutUpdated, so forcing a layout pass here makes
                // that fire exactly once with settled dimensions.
                PaneHostContainer.InvalidateMeasure();
                PaneHostContainer.UpdateLayout();
            }
        }
    }

    private void AddPane(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        paneHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        paneHost.VerticalAlignment = VerticalAlignment.Stretch;
        paneHost.Visibility = Visibility.Collapsed;
        PaneHostContainer.Children.Add(paneHost);
    }

    private void RemovePane(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        PaneHostContainer.Children.Remove(paneHost);
    }

    private void SwapActivePane()
    {
        var active = (PaneHost)_manager.ActiveTab.PaneHost;
        foreach (UIElement child in PaneHostContainer.Children)
        {
            child.Visibility = ReferenceEquals(child, active)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // Hover-expand state machine -----------------------------------------

    private void OnStripPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!HoverExpandEnabled) return;
        if (_state != VerticalTabStripState.Collapsed) return;
        if (_pinnedExpanded) return;
        if (IsUserCurrentlyTyping()) return;

        // Timer allocated lazily, Tick hooked ONCE for the life of the
        // host. Starting / stopping is idempotent; re-subscribing per
        // PointerEntered (as the earlier revision did) would duplicate
        // the handler every hover.
        if (_hoverEnterTimer is null)
        {
            _hoverEnterTimer = DispatcherQueue.CreateTimer();
            _hoverEnterTimer.IsRepeating = false;
            _hoverEnterTimer.Tick += OnHoverEnterTick;
        }
        _hoverEnterTimer.Interval = TimeSpan.FromMilliseconds(HoverEnterDelayMs);
        _hoverEnterTimer.Start();
    }

    private void OnHoverEnterTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_state != VerticalTabStripState.Collapsed) return;
        BeginHoverExpand();
    }

    private void OnStripPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!HoverExpandEnabled) return;
        _hoverEnterTimer?.Stop();
        if (_state != VerticalTabStripState.HoverExpanded) return;

        if (_hoverLeaveTimer is null)
        {
            _hoverLeaveTimer = DispatcherQueue.CreateTimer();
            _hoverLeaveTimer.IsRepeating = false;
            _hoverLeaveTimer.Tick += OnHoverLeaveTick;
        }
        _hoverLeaveTimer.Interval = TimeSpan.FromMilliseconds(HoverLeaveDelayMs);
        _hoverLeaveTimer.Start();
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
        // TickCount64 is a monotonic millisecond counter — immune to
        // NTP / DST jumps and cheaper than DateTime arithmetic.
        var elapsed = Environment.TickCount64 - _host.LastKeystrokeTick;
        return elapsed < TypingSuppressionMs;
    }

    private void BeginHoverExpand()
    {
        // Render the strip as an overlay: raise its Z-order (attached
        // Canvas.ZIndex works on any UIElement, including Grid children)
        // and give it an explicit Width so it floats over the terminal
        // column without resizing the ColumnDefinition. The terminal
        // content therefore does not reflow — that's the key difference
        // from pinned-expanded.
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

    // Active-tab title binding for the custom title bar.
    private TabModel? _titleBoundTab;
    private void RebindActiveTitle()
    {
        if (_titleBoundTab is not null)
            _titleBoundTab.PropertyChanged -= OnActiveTitlePropertyChanged;
        _titleBoundTab = _manager.ActiveTab;
        if (_titleBoundTab is not null)
            _titleBoundTab.PropertyChanged += OnActiveTitlePropertyChanged;
        UpdateTitleText();
    }

    private void OnActiveTitlePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabModel.EffectiveTitle) ||
            e.PropertyName == nameof(TabModel.ShellReportedTitle) ||
            e.PropertyName == nameof(TabModel.UserOverrideTitle))
        {
            UpdateTitleText();
        }
    }

    private void UpdateTitleText()
    {
        TitleText.Text = _titleBoundTab?.EffectiveTitle ?? "Ghostty";
    }

    /// <inheritdoc/>
    public Task RequestCloseTabAsync(TabModel tab) =>
        TabCloseConfirmation.RequestAsync(_manager, tab, XamlRoot);
}
