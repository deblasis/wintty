using System;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// The icon rail / row list visual for vertical tabs. Binds directly
/// to <see cref="TabManager.Tabs"/> via <c>ItemsSource</c> so WinUI
/// drives add/remove/move automatically, and syncs selection both
/// ways against <see cref="TabManager.ActiveTab"/>.
///
/// All row visuals live in XAML templates (see <c>VerticalTabStrip.xaml</c>).
/// The container style drives hover/selected backgrounds and the
/// selected-row accent bar through the standard ListViewItem visual
/// states — no LayoutUpdated polling, no Canvas overlay.
/// </summary>
internal sealed partial class VerticalTabStrip : UserControl
{
    private readonly TabManager _manager;
    private bool _syncing;
    private bool _isExpanded;

    /// <summary>Raised when the user clicks the chevron toggle.</summary>
    public event EventHandler? ChevronToggled;

    /// <summary>Raised when the user clicks the new-tab "+" button.</summary>
    public event EventHandler? NewTabRequested;

    /// <summary>Raised when a row's close button is clicked. The
    /// consuming <see cref="VerticalTabHost"/> routes this through its
    /// shared <c>RequestCloseTabAsync</c>.</summary>
    public event Func<TabModel, System.Threading.Tasks.Task>? CloseRequestedFromRow;

    /// <summary>
    /// Toggle between collapsed (40x40 icon-only) and expanded
    /// (32px, icon + title + close) row templates. Swapping
    /// <c>ItemTemplate</c> is the clean way to change row layout at
    /// runtime without rebuilding containers imperatively.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            TabList.ItemTemplate = (DataTemplate)Resources[
                value ? "ExpandedRowTemplate" : "CollapsedRowTemplate"];
            // Chevron points in the direction the strip will go on
            // the next click: right (E76C) when collapsed, left
            // (E76B) when expanded.
            ChevronIcon.Glyph = value ? "\uE76B" : "\uE76C";
        }
    }

    public VerticalTabStrip(TabManager manager)
    {
        InitializeComponent();
        _manager = manager;

        TabList.ItemsSource = _manager.Tabs;
        SyncSelectionFromManager();

        _manager.ActiveTabChanged += (_, _) => SyncSelectionFromManager();
    }

    private void SyncSelectionFromManager()
    {
        // Guard against the bounce: our handler for SelectionChanged
        // calls Activate, which fires ActiveTabChanged, which calls
        // back into here. The _syncing flag breaks the loop.
        if (_syncing) return;
        _syncing = true;
        try
        {
            TabList.SelectedItem = _manager.ActiveTab;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (TabList.SelectedItem is not TabModel tab) return;
        _syncing = true;
        try
        {
            _manager.Activate(tab);
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnRowCloseClick(object sender, RoutedEventArgs e)
    {
        // Tag is set to the bound TabModel in the expanded row template.
        if (sender is FrameworkElement { Tag: TabModel tab } &&
            CloseRequestedFromRow is { } handler)
        {
            await handler(tab);
        }
    }

    private void OnChevronClick(object sender, RoutedEventArgs e) =>
        ChevronToggled?.Invoke(this, EventArgs.Empty);

    private void OnNewTabClick(object sender, RoutedEventArgs e) =>
        NewTabRequested?.Invoke(this, EventArgs.Empty);
}
