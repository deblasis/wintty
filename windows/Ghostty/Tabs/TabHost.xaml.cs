using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Panes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// WinUI host that visualises a <see cref="TabManager"/> as a
/// <see cref="TabView"/>. Owns the bidirectional mapping between
/// <see cref="TabModel"/>s and <see cref="TabViewItem"/>s.
/// </summary>
internal sealed partial class TabHost : UserControl, ITabHost
{
    private readonly TabManager _manager;
    private readonly Dictionary<TabModel, TabViewItem> _itemByModel = new();
    private readonly Dictionary<TabViewItem, TabModel> _modelByItem = new();
    // Keep a strong reference to each per-tab PropertyChanged handler so
    // RemoveItem can detach it. A raw lambda closure could not be unhooked,
    // which leaked the TabViewItem for the lifetime of the TabModel.
    private readonly Dictionary<TabModel, PropertyChangedEventHandler> _headerHandlers = new();
    private bool _suppressSelectionEvent;

    public FrameworkElement HostElement => this;

    public UIElement DragRegion => CustomDragRegion;

    public TabHost(TabManager manager)
    {
        InitializeComponent();
        _manager = manager;

        foreach (var t in _manager.Tabs) AddItem(t);
        SelectActive();

        _manager.TabAdded += (_, t) => { AddItem(t); SelectActive(); };
        _manager.TabRemoved += (_, t) => RemoveItem(t);
        _manager.TabMoved += (_, e) => MoveItem(e.tab, e.to);
        _manager.ActiveTabChanged += (_, _) => SelectActive();
    }

    private void AddItem(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        paneHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        paneHost.VerticalAlignment = VerticalAlignment.Stretch;
        paneHost.Visibility = Visibility.Collapsed;
        PaneHostContainer.Children.Add(paneHost);

        var item = new TabViewItem
        {
            Header = tab.EffectiveTitle,
            Content = null,
            ContextFlyout = TabContextMenuBuilder.Build(_manager, tab, RequestCloseTabAsync),
            DataContext = tab,
        };

        // Named handler retained in a dictionary so RemoveItem can
        // unhook it. A lambda captured inline would be unreachable
        // and leak the TabViewItem.
        PropertyChangedEventHandler handler = (_, _) => RefreshHeader(item, tab);
        tab.PropertyChanged += handler;
        _headerHandlers[tab] = handler;

        _itemByModel[tab] = item;
        _modelByItem[item] = tab;
        TabViewControl.TabItems.Add(item);
    }

    private void RemoveItem(TabModel tab)
    {
        if (!_itemByModel.TryGetValue(tab, out var item)) return;

        if (_headerHandlers.TryGetValue(tab, out var handler))
        {
            tab.PropertyChanged -= handler;
            _headerHandlers.Remove(tab);
        }

        TabViewControl.TabItems.Remove(item);
        _itemByModel.Remove(tab);
        _modelByItem.Remove(item);

        var paneHost = (PaneHost)tab.PaneHost;
        PaneHostContainer.Children.Remove(paneHost);
    }

    private void MoveItem(TabModel tab, int to)
    {
        if (!_itemByModel.TryGetValue(tab, out var item)) return;
        TabViewControl.TabItems.Remove(item);
        TabViewControl.TabItems.Insert(to, item);
    }

    private void SelectActive()
    {
        if (!_itemByModel.TryGetValue(_manager.ActiveTab, out var item)) return;

        var activePaneHost = (PaneHost)_manager.ActiveTab.PaneHost;
        foreach (UIElement child in PaneHostContainer.Children)
        {
            child.Visibility = ReferenceEquals(child, activePaneHost)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (ReferenceEquals(TabViewControl.SelectedItem, item)) return;
        _suppressSelectionEvent = true;
        TabViewControl.SelectedItem = item;
        _suppressSelectionEvent = false;
    }

    private void RefreshHeader(TabViewItem item, TabModel tab)
    {
        item.Header = tab.EffectiveTitle;
    }

    private void OnAddTabButtonClick(TabView sender, object args) => _manager.NewTab();

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // async void is forced by the WinUI event signature. An unhandled
        // exception from an async void method terminates the process, so
        // we catch at the boundary — but log loudly rather than swallow.
        try
        {
            if (args.Item is TabViewItem item &&
                _modelByItem.TryGetValue(item, out var model))
            {
                await RequestCloseTabAsync(model);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TabHost] OnTabCloseRequested failed: {ex}");
            System.Diagnostics.Debug.Fail("Tab close failed", ex.ToString());
        }
    }

    /// <summary>
    /// Single entry point for every "close this tab" path: per-tab
    /// X button, middle-click, context-menu Close, and the keyboard
    /// chord (via <see cref="MainWindow"/>'s accelerator handler).
    /// Shows the multi-pane confirmation dialog when needed and only
    /// then calls <see cref="TabManager.CloseTab"/>.
    /// </summary>
    public Task RequestCloseTabAsync(TabModel tab) =>
        TabCloseConfirmation.RequestAsync(_manager, tab, XamlRoot);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvent) return;
        if (TabViewControl.SelectedItem is TabViewItem item &&
            _modelByItem.TryGetValue(item, out var model))
        {
            _manager.Activate(model);
        }
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        if (args.Item is TabViewItem item &&
            _modelByItem.TryGetValue(item, out var model))
        {
            var newIndex = TabViewControl.TabItems.IndexOf(item);
            var oldIndex = _manager.IndexOf(model);
            if (oldIndex != newIndex && oldIndex >= 0)
                _manager.Move(oldIndex, newIndex);
        }
    }
}
