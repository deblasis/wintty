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
/// Vertical tabs are out of scope for this PR — they come in plan 2
/// as a sibling user control sharing this same TabManager.
/// </summary>
internal sealed partial class TabHost : UserControl, ITabHost
{
    private readonly TabManager _manager;
    private readonly PaneActionRouter _router;
    private readonly DialogTracker _dialogs;
    private readonly Dictionary<TabModel, TabViewItem> _itemByModel = new();
    private bool _suppressSelectionEvent;

    public FrameworkElement HostElement => this;

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
        // via a shared container (see #171), so both tab hosts can
        // coexist without double-parenting the same PaneHost.
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
        headerPanel.Children.Add(headerText);
        headerPanel.Children.Add(headerBar);

        // Tab color tint. We paint headerPanel.Background, not
        // TabViewItem.Background: the WinUI 3 TabView template layers
        // its own brushes over the item background and the tint gets
        // composited away on 1.6. The header panel is our own XAML so
        // we own the paint surface outright.
        ApplyTabColor(headerPanel, tab.Color, selected: false);

        var item = new TabViewItem
        {
            Header = headerPanel,
            Content = null,
            ContextFlyout = TabContextMenuBuilder.Build(_manager, tab, RequestCloseTabAsync, _dialogs),
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
                var selected = ReferenceEquals(_manager.ActiveTab, tab);
                ApplyTabColor(headerPanel, tab.Color, selected);
            }
        };
        _itemByModel[tab] = item;
        TabViewControl.TabItems.Add(item);
    }

    private void RemoveItem(TabModel tab)
    {
        if (!_itemByModel.TryGetValue(tab, out var item)) return;
        TabViewControl.TabItems.Remove(item);
        _itemByModel.Remove(tab);
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
        // shared container (see #171). This method only syncs the
        // TabView strip selection.
        if (!_itemByModel.TryGetValue(_manager.ActiveTab, out var item)) return;

        // Re-apply tab color tints so the selected tab gets the
        // stronger alpha (0.6) and the others get 0.35. The previous
        // selected tab's header will flip from 0.6 to 0.35 here.
        foreach (var (model, viewItem) in _itemByModel)
        {
            if (viewItem.Header is StackPanel headerPanel)
            {
                var isSelected = ReferenceEquals(model, _manager.ActiveTab);
                ApplyTabColor(headerPanel, model.Color, isSelected);
            }
        }

        if (ReferenceEquals(TabViewControl.SelectedItem, item)) return;
        _suppressSelectionEvent = true;
        TabViewControl.SelectedItem = item;
        _suppressSelectionEvent = false;
    }

    // Tab color alpha values. Selected tabs use a stronger tint so
    // the color is clearly visible; unselected tabs use a lighter
    // tint so Mica/acrylic shows through and text stays readable.
    private const byte SelectedTabAlpha = 153;   // ~0.6 of 255
    private const byte UnselectedTabAlpha = 89;  // ~0.35 of 255

    /// <summary>
    /// Paint the tab header background from a <see cref="TabColor"/>.
    /// None clears to transparent. Non-None uses fixed sRGB at alpha
    /// 0.35 unselected / 0.6 selected so Mica/acrylic shows through
    /// and the text foreground stays readable on both themes.
    /// </summary>
    private static void ApplyTabColor(StackPanel headerPanel, TabColor color, bool selected)
    {
        if (color == TabColor.None)
        {
            headerPanel.Background = null;
            return;
        }
        var drawing = TabColorPalette.Colors[color];
        var alpha = selected ? SelectedTabAlpha : UnselectedTabAlpha;
        headerPanel.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(alpha, drawing.R, drawing.G, drawing.B));
    }

    private void OnAddTabButtonClick(TabView sender, object args) => _manager.NewTab();

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
        var activeTextBrush = new SolidColorBrush(theme.ActiveTabText);
        var tabBgBrush = new SolidColorBrush(theme.TabBarBackground);

        // Background resources on TabViewControl work with a theme toggle.
        TabViewControl.Resources["TabViewBackground"] = tabBgBrush;
        TabViewControl.Resources["TabViewItemHeaderBackgroundSelected"] = accentBrush;

        // Toggle theme to force WinUI to re-read background resources.
        TabViewControl.RequestedTheme = ElementTheme.Light;
        TabViewControl.RequestedTheme = _cachedTheme;

        // For foreground: use a HeaderTemplate with a TextBlock whose
        // Foreground is bound to our brush. This survives Header
        // re-assignments (title changes) because the template is
        // persistent -- only the Header string changes, not the
        // template that renders it.
        _shellActiveTextBrush = activeTextBrush;
        var template = CreateColoredHeaderTemplate(activeTextBrush);
        foreach (var item in TabViewControl.TabItems)
        {
            if (item is TabViewItem tvi)
                tvi.HeaderTemplate = template;
        }
    }

    private SolidColorBrush? _shellActiveTextBrush;

    private static DataTemplate CreateColoredHeaderTemplate(SolidColorBrush brush)
    {
        // Build a DataTemplate programmatically: a TextBlock that
        // displays the Header string with our foreground color.
        var c = brush.Color;
        var xaml = $@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
            <TextBlock Text='{{Binding}}' Foreground='#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}'
                       TextTrimming='CharacterEllipsis'/>
        </DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private ElementTheme _cachedTheme = ElementTheme.Default;


    /// <summary>
    /// Remove shell theme overrides so the TabView reverts to
    /// its default theme resources.
    /// </summary>
    internal void ClearShellTheme()
    {
        TabViewControl.Resources.Remove("TabViewBackground");
        TabViewControl.Resources.Remove("TabViewItemHeaderBackgroundSelected");
        _shellActiveTextBrush = null;

        // Remove custom header templates so tabs revert to default rendering.
        foreach (var item in TabViewControl.TabItems)
        {
            if (item is TabViewItem tvi)
                tvi.ClearValue(TabViewItem.HeaderTemplateProperty);
        }

        TabViewControl.RequestedTheme = ElementTheme.Light;
        TabViewControl.RequestedTheme = _cachedTheme;
    }

    internal void SetRequestedTheme(ElementTheme theme)
    {
        _cachedTheme = theme;
        RequestedTheme = theme;
    }

    /// <summary>
    /// Set the accent color used for the selected tab indicator.
    /// Driven by cursor-color from the terminal config.
    /// </summary>
    internal void SetAccentColor(Windows.UI.Color color)
    {
        var c = Microsoft.UI.ColorHelper.FromArgb(color.A, color.R, color.G, color.B);
        TabViewControl.Resources["TabViewItemHeaderBackgroundSelected"] =
            new SolidColorBrush(c);
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
