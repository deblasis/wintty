using System;
using System.Linq;
using Ghostty.Core.Input;
using Ghostty.Interop;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ghostty.Settings.Pages;

/// <summary>Picks header vs entry template by row type (AOT-safe, no reflection).</summary>
internal sealed class KeybindRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? EntryTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is KeybindCategoryHeader ? HeaderTemplate : EntryTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

internal sealed partial class KeybindingsPage : Page
{
    // Concrete ConfigService is used (not IConfigService) because the
    // libghostty config handle (ConfigHandle) the enumerate ABI needs is
    // only exposed on the concrete type; the interface only carries
    // ConfigChanged.
    private readonly ConfigService _configService;
    private readonly Ghostty.Core.Config.IConfigFileEditor _editor;
    private KeybindCatalog _catalog;

    // The row whose context menu is currently open. WinUI doesn't reliably
    // populate MenuFlyout.Target for a ContextFlyout, so we capture the
    // right-tapped row's item here (the row Grid's Tag) instead of walking
    // the flyout's target chain. Each container's RightTapped handler reads
    // grid.Tag, which OnContainerContentChanging refreshes on every realize.
    private KeybindListItem? _contextRowItem;

    public KeybindingsPage(ConfigService configService, Ghostty.Core.Config.IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();

        _catalog = KeybindCatalog.Build(Array.Empty<EnumeratedKeybind>());
        BindingsList.ContainerContentChanging += OnContainerContentChanging;

        // Subscribe in Loaded (not the ctor): SettingsWindow caches pages and
        // reuses the instance, so the ctor runs once but Loaded/Unloaded fire on
        // every navigation. Pairing here keeps the subscription correct across
        // re-shows (matches ColorsPage / RawEditorPage).
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged += OnConfigChanged;
        Rebuild(); // refresh in case the config changed while detached
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => _configService.ConfigChanged -= OnConfigChanged;

    private void OnConfigChanged(Ghostty.Core.Config.IConfigService _)
    {
        if (DispatcherQueue.HasThreadAccess) Rebuild();
        else DispatcherQueue.TryEnqueue(Rebuild);
    }

    private void Rebuild()
    {
        var binds = KeybindEnumerator.Enumerate(_configService.ConfigHandle);
        var defaults = _configService.EnumerateDefaultKeybinds();
        _catalog = KeybindCatalog.Build(binds, defaults);
        ApplyFilter();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        var root = args.ItemContainer.ContentTemplateRoot;
        switch (args.Item)
        {
            case KeybindCategoryHeader header when root is TextBlock headerText:
                headerText.Text = header.Name;
                break;
            case KeybindListItem item when root is Grid grid:
                if (grid.FindName("FriendlyText") is TextBlock f) f.Text = item.Friendly;
                if (grid.FindName("LabelText") is TextBlock l) l.Text = item.Label;
                if (grid.FindName("ConflictIcon") is FontIcon icon)
                {
                    if (item.Conflict.HasConflict)
                    {
                        icon.Visibility = Visibility.Visible;
                        ToolTipService.SetToolTip(icon, item.Conflict.Message);
                    }
                    else
                    {
                        icon.Visibility = Visibility.Collapsed;
                        ToolTipService.SetToolTip(icon, null);
                    }
                }
                if (grid.FindName("UserTag") is FrameworkElement tag)
                    tag.Visibility = item.Source == KeybindSource.User
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                // Stash the item on the container so the context-menu handlers
                // can resolve the right-clicked row. Containers are recycled,
                // so refresh the Tag every realize. RightTapped is wired with
                // -=/+= so the recycled container never accumulates handlers.
                grid.Tag = item;
                grid.RightTapped -= Row_RightTapped;
                grid.RightTapped += Row_RightTapped;
                break;
        }
    }

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
        => _contextRowItem = (sender as FrameworkElement)?.Tag as KeybindListItem;

    private void UnbindItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextRowItem is not { } item) return;
        ApplyUserEdit(item, UserKeybindEditor.Unbind);
    }

    private void ResetItem_Click(object sender, RoutedEventArgs e)
    {
        // Reset only makes sense for a user customization; a pure default row
        // has nothing in the file to remove.
        if (_contextRowItem is not { } item || item.Source != KeybindSource.User) return;
        ApplyUserEdit(item, UserKeybindEditor.Reset);
    }

    private void ApplyUserEdit(
        KeybindListItem item,
        Func<string[], EnumeratedKeybind, string[]> op)
    {
        // The list item carries only the friendly label, not the trigger
        // steps the editor needs. Re-find the live EnumeratedKeybind by
        // matching its action + the same label the row was built from.
        var binds = KeybindEnumerator.Enumerate(_configService.ConfigHandle);
        var match = binds.FirstOrDefault(b =>
            b.Action == item.RawAction && TriggerLabeler.Describe(b) == item.Label);
        if (match is null) return;

        var current = _editor.GetRepeatableValues("keybind");
        var updated = op(current, match);
        _editor.SetRepeatableValues("keybind", updated);
        _configService.Reload(); // raises ConfigChanged -> Rebuild
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    private void ConflictsToggle_Toggled(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
        => BindingsList.ItemsSource = _catalog.Filter(SearchBox.Text, ConflictsToggle.IsChecked == true);
}
