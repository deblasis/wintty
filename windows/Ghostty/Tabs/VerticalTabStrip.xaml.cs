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
        Loaded += (_, _) => RefreshSelectionChrome();

        _manager.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _manager.ActiveTabChanged += (_, _) => SyncSelectionFromManager();
    }

    internal SolidColorBrush AccentBrush =>
        Resources.TryGetValue("StripAccentBrush", out var res) && res is SolidColorBrush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);

    /// <summary>
    /// Opaque pane chrome from the terminal palette when window-theme=wintty.
    /// </summary>
    internal void ApplyShellChrome(ShellThemeService theme, SolidColorBrush paneBg)
    {
        _shellThemeActive = true;
        Background = paneBg;
        _stripBackdropPacked = PackColor(theme.TabBarBackground);
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

        uint tabBgPacked = PackColor(theme.TabBarBackground);
        _shellInactiveTextBrush = new SolidColorBrush(
            ThemeResolution.PreferLightForeground(tabBgPacked)
                ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xB3, 0x00, 0x00, 0x00));

        ApplySelectedForegroundResources(_shellActiveTextBrush);
        ApplyInactiveForegroundResources(_shellInactiveTextBrush);

        var hoverBg = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
        var pressedBg = ResolveThemeBrush("SubtleFillColorTertiaryBrush");
        SetNavResource("NavigationViewItemBackgroundPointerOver", hoverBg);
        SetNavResource("NavigationViewItemBackgroundPressed", pressedBg);

        RefreshNavViewTheme();
        RecolorNavItems();
        RefreshSelectionChrome();
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

        var paneBg = ResolveThemeBrush("LayerFillColorDefaultBrush");
        Background = paneBg;
        _stripBackdropPacked = PackColor(paneBg.Color);
        ApplyTransparentNavPaneSurface();

        ApplyDefaultSelectedTabResources();

        ClearNavResource("NavigationViewItemForeground");
        ClearNavResource("NavigationViewItemForegroundPointerOver");
        ClearNavResource("NavigationViewItemForegroundSelected");
        ClearNavResource("NavigationViewItemForegroundSelectedPointerOver");

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

        // Tab-bar backdrop for preset tint blending is owned by
        // ApplyShellChrome / ApplyDefaultPaneChrome. Terminal bg != tab bar.
        if (!_shellThemeActive)
            _stripBackdropPacked = PackColor(background);

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

    private void UpdateSelectionRow()
    {
        if (_selectionRowSuppressed)
        {
            SelectionRow.Visibility = Visibility.Collapsed;
            return;
        }

        if (_manager.ActiveTab is null
            || !_items.TryGetValue(_manager.ActiveTab, out var item)
            || item.ActualWidth <= 0
            || item.ActualHeight <= 0
            || ActualWidth <= 0)
        {
            SelectionRow.Visibility = Visibility.Collapsed;
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
        SelectionRow.Visibility = Visibility.Visible;
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
        if (tab.Color != TabColor.None)
        {
            return (
                TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: true)),
                TabColorBrush.FromPackedRgb(TabColorPalette.ForegroundRgb(
                    tab.Color, selected: true, _stripBackdropPacked)));
        }

        var fill = ResolveSelectionRowFill(tab);
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
            ThemeResolution.PreferLightForeground(_stripBackdropPacked)
                ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
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
    internal void SyncSelectionFromManager()
    {
        if (_syncing) return;
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

    private void SetNavResource(string key, Brush brush) => NavView.Resources[key] = brush;

    private void ClearNavResource(string key) => NavView.Resources.Remove(key);

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
        foreach (var tab in _manager.Tabs)
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
        ToolTipService.SetToolTip(item, tab.EffectiveTitle);

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
            ToolTipService.SetToolTip(navItem, tab.EffectiveTitle);
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
        if (index >= 0 && index <= NavView.MenuItems.Count)
            NavView.MenuItems.Insert(index, item);
        else
            NavView.MenuItems.Add(item);
        ApplyItemTabColor(item, tab);
    }

    private void OnRowCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabModel tab })
            CloseRequestedFromRow?.Invoke(tab);
    }

    private void RemoveItem(TabModel tab)
    {
        if (!_items.TryGetValue(tab, out var item)) return;
        NavView.MenuItems.Remove(item);
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
