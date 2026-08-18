using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Input;
using Ghostty.Panes;
using Ghostty.Services;
using Microsoft.UI.Xaml;
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

    public FrameworkElement HostElement => this;

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

        _manager.TabAdded += (_, t) => { AddItem(t); SelectActive(); };
        _manager.TabRemoved += (_, t) => RemoveItem(t);
        _manager.TabMoved += (_, e) => MoveItem(e.tab, e.to);
        _manager.ActiveTabChanged += (_, _) => SelectActive();
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
                getSnapSource: GetSnapSource,
                detachWithZone: DetachWithZone),
            DataContext = tab,
        };
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabModel.EffectiveTitle) ||
                e.PropertyName == nameof(TabModel.ShellReportedTitle) ||
                e.PropertyName == nameof(TabModel.UserOverrideTitle))
            {
                headerText.Text = tab.EffectiveTitle;
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
            }
        };
        _itemByModel[tab] = item;
        _headerTextByModel[tab] = headerText;
        TabViewControl.TabItems.Add(item);
        ApplyTabChrome(item, headerPanel, tab, selected: false);
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
        TabViewControl.TabItems.Remove(item);
        TabViewControl.TabItems.Insert(to, item);
        // _paneHostContainer order does not matter — Visibility picks
        // the active one. No reorder needed there.
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
            normalHandle = DrawingColorBrush(TabColorPalette.Background(tab.Color, selected: false));
            selectedHandle = DrawingColorBrush(TabColorPalette.Background(tab.Color, selected: true));
        }
        else if (selected && _selectedTabFillBrush is not null)
        {
            selectedHandle = _selectedTabFillBrush;
        }

        ApplyTabViewItemHeaderBrushes(viewItem, normalHandle, selectedHandle);
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

    private static SolidColorBrush DrawingColorBrush(System.Drawing.Color drawing)
        => new(Windows.UI.Color.FromArgb(
            drawing.A, drawing.R, drawing.G, drawing.B));

    private SolidColorBrush TabColorForegroundBrush(TabModel tab, bool selected)
    {
        var packed = TabColorPalette.ForegroundRgb(
            tab.Color, selected, _stripBackdropPacked);
        return new SolidColorBrush(UnpackColor(packed));
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

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
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
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Apply palette-derived colors to the tab strip.
    /// Called by MainWindow when shell theme changes.
    /// </summary>
    internal void ApplyShellTheme(ShellThemeService theme)
    {
        if (!theme.IsEnabled) return;

        var accentBrush = new SolidColorBrush(theme.AccentColor);
        var tabBgBrush = new SolidColorBrush(theme.TabBarBackground);
        _selectedTabFillBrush = accentBrush;
        _stripBackdropPacked = PackColor(theme.TabBarBackground);

        // Background resources on TabViewControl work with a theme toggle.
        TabViewControl.Resources["TabViewBackground"] = tabBgBrush;
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
        // Inactive tabs sit on the near-bg tab-bar background, not the
        // accent background, so the active brush gives them zero contrast
        // (#342). Pick a luminance-readable foreground over the tab-bar
        // background and mute it to ~70% so inactive reads as inactive.
        //
        // Calibrate the active title against the accent it sits on. The raw
        // ActiveTabText (cursor-text, or the bg fallback) can land at the
        // same luminance pole as the accent for some palettes (both light or
        // both dark), which erases the title. Keep it when it contrasts;
        // otherwise drop to a readable black/white. (#342)
        uint accentPacked = PackColor(theme.AccentColor);
        uint activePacked = PackColor(theme.ActiveTabText);
        _shellActiveTextBrush = new SolidColorBrush(UnpackColor(
            ThemeResolution.EnsureReadableForeground(accentPacked, activePacked)));

        uint tabBgPacked = PackColor(theme.TabBarBackground);
        var inactiveColor = ThemeResolution.PreferLightForeground(tabBgPacked)
            ? Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)  // white @ ~70%
            : Windows.UI.Color.FromArgb(0xB3, 0x00, 0x00, 0x00); // black @ ~70%
        _shellInactiveTextBrush = new SolidColorBrush(inactiveColor);
        RefreshTabColors();
    }

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

    private static Windows.UI.Color UnpackColor(uint packed) =>
        Windows.UI.Color.FromArgb(0xFF,
            (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);

    private ElementTheme _cachedTheme = ElementTheme.Default;


    /// <summary>
    /// Remove shell theme overrides so the TabView reverts to
    /// its default theme resources.
    /// </summary>
    internal void ClearShellTheme()
    {
        TabViewControl.Resources.Remove("TabViewBackground");
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
        _defaultActiveTextBrush = new SolidColorBrush(UnpackColor(
            ThemeResolution.EnsureReadableForeground(
                PackColor(background), PackColor(foreground))));

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
