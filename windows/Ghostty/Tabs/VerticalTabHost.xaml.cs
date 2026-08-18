using System;
using System.Threading.Tasks;
using Ghostty.Core.Config;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Panes;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Ghostty.Tabs;

/// <summary>
/// Vertical-sidebar tab host backed by Fluent NavigationView.
/// </summary>
internal sealed partial class VerticalTabHost : UserControl, ITabHost
{
    private readonly TabManager _manager;
    private readonly PaneActionRouter _router;
    private readonly DialogTracker _dialogs;
    private readonly VerticalTabStrip _strip;
    private readonly ColumnDragHandle _dragHandle;

    private FrameworkElement? _primaryIconBadge;

    /// <summary>
    /// Title-row badge in normal vertical mode; strip fallback when the
    /// vertical title bar is suppressed (quake window).
    /// </summary>
    public FrameworkElement IconBadge => _primaryIconBadge ?? IconBadgeHost;

    internal void SetPrimaryIconBadge(FrameworkElement badge) => _primaryIconBadge = badge;

    /// <summary>
    /// Offset strip chrome below the 34px title row, or flush when the
    /// title bar is suppressed and the strip icon is shown instead.
    /// </summary>
    internal void ConfigureTitleBarIconMode(bool titleBarSuppressed)
    {
        StripRoot.Margin = new Thickness(
            0,
            titleBarSuppressed ? 0 : Ghostty.Shell.LayoutCoordinator.VerticalTitleBarHeight,
            0,
            0);
        IconBadgeHost.Visibility = titleBarSuppressed
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshSelectionChrome();
    }

    private double _expandedWidth = WindowsOnlyKeyParsers.VerticalTabsWidthDefault;
    private bool _pinnedExpanded;
    private bool _shellThemeActive;

    public FrameworkElement HostElement => this;
    public UIElement DragRegion => this;

    public event EventHandler<double>? StripWidthChangeRequested;

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
        _ = host;

        _strip = new VerticalTabStrip(manager);
        _strip.OpenPaneLength = _expandedWidth;
        _strip.ApplyPaneLayout(
            expanded: false,
            width: Ghostty.Shell.LayoutCoordinator.VerticalStripCollapsedWidth);
        _strip.CloseRequestedFromRow += async tab => await RequestCloseTabAsync(tab);
        StripHost.Content = _strip;

        _dragHandle = new ColumnDragHandle(
            onWidthChanged: w =>
            {
                _strip.OpenPaneLength = w;
                StripColumn.Width = new GridLength(w);
                StripWidthChangeRequested?.Invoke(this, w);
            },
            readCurrentWidth: () => StripColumn.Width.Value)
        {
            Visibility = Visibility.Collapsed,
            Height = double.NaN,
        };
        HandleHost.Children.Add(_dragHandle);
        HandleHost.SizeChanged += (_, e) => _dragHandle.Height = e.NewSize.Height;

        ApplyFromConfig(App.ConfigService);
        if (App.ConfigService is { } cfg)
        {
            cfg.ConfigChanged += OnConfigChanged;
            Unloaded += (_, _) => cfg.ConfigChanged -= OnConfigChanged;
        }

        UpdatePaneToggleChrome();
    }

    private void OnConfigChanged(IConfigService cfg) => ApplyFromConfig(cfg);

    private void ApplyFromConfig(IConfigService? cfg)
    {
        _expandedWidth = cfg?.VerticalTabsWidth
            ?? WindowsOnlyKeyParsers.VerticalTabsWidthDefault;
        var wantPinned = cfg?.VerticalTabsPinned ?? false;

        // Cold start: honor pinned without firing the tween, because
        // LayoutCoordinator has not subscribed to StripWidthChangeRequested
        // yet and the event would be dropped. Reloads go through ApplyPinned
        // so the outer column actually moves.
        if (StripWidthChangeRequested is null)
        {
            _pinnedExpanded = wantPinned;
            _dragHandle.Visibility = wantPinned ? Visibility.Visible : Visibility.Collapsed;
            StripColumn.Width = new GridLength(CurrentStripTarget);
            _strip.ApplyPaneLayout(wantPinned, CurrentStripTarget);
            UpdatePaneToggleChrome();
            return;
        }

        _strip.OpenPaneLength = _expandedWidth;
        if (_pinnedExpanded == wantPinned)
        {
            // Width may still have changed under a pinned sidebar.
            if (_pinnedExpanded)
            {
                StripWidthChangeRequested?.Invoke(this, CurrentStripTarget);
                _strip.ApplyPaneLayout(true, CurrentStripTarget);
            }
            return;
        }
        ApplyPinned(wantPinned);
    }

    internal void TogglePinnedFromKeyboard() => TogglePinned();

    internal void AttachOwner(MainWindow owner) => NewTabButton.Owner = owner;

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => TogglePinned();

    private void TogglePinned() => ApplyPinned(!_pinnedExpanded);

    private void ApplyPinned(bool pinned)
    {
        if (_pinnedExpanded == pinned) return;

        _pinnedExpanded = pinned;
        _dragHandle.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        UpdatePaneToggleChrome();

        // Snap outer column first, then switch MUXC into pane-only mode.
        StripWidthChangeRequested?.Invoke(this, CurrentStripTarget);
        _strip.ApplyPaneLayout(_pinnedExpanded, CurrentStripTarget);
    }

    private void UpdatePaneToggleChrome()
    {
        ToolTipService.SetToolTip(
            PaneToggleButton,
            _pinnedExpanded ? "Collapse sidebar" : "Expand sidebar");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PaneToggleButton,
            _pinnedExpanded ? "Collapse sidebar" : "Expand sidebar");
        // E700 = GlobalNavigationButton, E76C = chevron-right when expanded.
        PaneToggleIcon.Glyph = _pinnedExpanded ? "\uE76C" : "\uE700";
    }

    internal void SetInternalStripWidth(double width)
    {
        StripColumn.Width = new GridLength(width);
        var collapsed = Ghostty.Shell.LayoutCoordinator.VerticalStripCollapsedWidth;
        _strip.ApplyPaneLayout(width > collapsed, width);
    }

    private void OnStripContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_strip.TabFromElement(source) is { } tab)
        {
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

            var row = VisualTreeHelperEx.FindAncestor<NavigationViewItem>(source);
            if (row is not null)
            {
                if (e.TryGetPosition(row, out Point tabPos))
                    tabFlyout.ShowAt(row, new FlyoutShowOptions { Position = tabPos });
                else
                    tabFlyout.ShowAt(row);
            }
            else
            {
                tabFlyout.ShowAt((FrameworkElement)sender);
            }
            e.Handled = true;
            return;
        }

        var flyout = StripContextMenuBuilder.Build(
            _manager,
            _router,
            isVertical: true,
            isSidebarCollapsed: !_pinnedExpanded);

        var anchor = (FrameworkElement)sender;
        if (e.TryGetPosition(anchor, out Point position))
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        else
            flyout.ShowAt(anchor);
        e.Handled = true;
    }

    public async Task RequestCloseTabAsync(TabModel tab)
        => await TabCloseConfirmation.RequestAsync(_manager, tab, XamlRoot, _dialogs);

    internal void ApplyShellTheme(ShellThemeService theme)
    {
        if (!theme.IsEnabled) return;

        _shellThemeActive = true;
        var tabBgBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
            theme.TabBarBackground.A, theme.TabBarBackground.R,
            theme.TabBarBackground.G, theme.TabBarBackground.B));
        Background = tabBgBrush;
        StripRoot.Background = tabBgBrush;
        _strip.ApplyShellChrome(theme, tabBgBrush);
    }

    internal void ClearShellTheme()
    {
        _shellThemeActive = false;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        StripRoot.ClearValue(Grid.BackgroundProperty);
        _strip.ApplyDefaultPaneChrome(RequestedTheme);
    }

    internal void SetRequestedTheme(ElementTheme theme)
    {
        RequestedTheme = theme;
        if (!_shellThemeActive)
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _strip.ApplyDefaultPaneChrome(theme);
        }
        else
            // Element theme still drives icon/hamburger brushes when shell
            // theme owns the pane fill -- refresh so MUXC re-reads resources.
            _strip.RefreshNavViewTheme();
    }

    internal void SetAccentColor(Windows.UI.Color color)
    {
        if (_strip.Resources.TryGetValue("StripAccentBrush", out var res)
            && res is SolidColorBrush brush)
        {
            brush.Color = Microsoft.UI.ColorHelper.FromArgb(
                color.A, color.R, color.G, color.B);
        }
    }

    /// <summary>
    /// Default-path selected-tab fill -- terminal background so the
    /// active row connects to the pane, matching horizontal TabHost.
    /// </summary>
    internal void SetSelectedTabColors(Windows.UI.Color background, Windows.UI.Color foreground)
        => _strip.SetSelectedTabColors(background, foreground);

    internal void RefreshSelectionChrome() => _strip.RefreshSelectionChrome();

    internal void SyncSelectionFromManager() => _strip.SyncSelectionFromManager();

    internal void RefreshTabColors() => _strip.RefreshTabColors();
}
