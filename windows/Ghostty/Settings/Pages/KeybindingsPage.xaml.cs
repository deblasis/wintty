using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Core.Input;
using Ghostty.Interop;
using Ghostty.Logging;
using Ghostty.Services;
using Microsoft.Extensions.Logging;
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
    private IReadOnlyList<EnumeratedKeybind> _binds = Array.Empty<EnumeratedKeybind>();
    private IReadOnlyList<EnumeratedKeybind> _defaults = Array.Empty<EnumeratedKeybind>();

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

        Map.ModifierChanged += (_, _) => ApplyMap();
        Map.KeyClicked += Map_KeyClicked;

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
        _binds = KeybindEnumerator.Enumerate(_configService.ConfigHandle);
        _defaults = _configService.EnumerateDefaultKeybinds();
        _catalog = KeybindCatalog.Build(_binds, _defaults);
        ApplyFilter();
        if (KeyboardPanel.Visibility == Visibility.Visible) ApplyMap();
    }

    private void ApplyMap()
        => Map.Apply(KeyboardMapModel.Build(_binds, _defaults, Map.ModifierMask));

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

    private async void RebindItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextRowItem is not { } item) return;

        // Pass the live finalized bind set so the dialog can warn about
        // assign-time conflicts against what is actually in effect.
        var current = KeybindEnumerator.Enumerate(_configService.ConfigHandle);
        var dialog = new Ghostty.Settings.RebindDialog(current, item.RawAction, item.Friendly)
        {
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || dialog.CapturedTrigger is not { } token) return;

        // Add/override: write the new trigger=action line (dropping any user
        // line already at that chord). The action's other triggers stay intact.
        TryWriteKeybinds(current =>
            UserKeybindEditor.Assign(current, token, item.RawAction));
    }

    private void ApplyUserEdit(
        KeybindListItem item,
        Func<string[], EnumeratedKeybind, string[]> op)
    {
        // The list item carries only the friendly label, not the trigger
        // steps the editor needs. Re-find the live EnumeratedKeybind by
        // matching its action + the same label the row was built from.
        var binds = KeybindEnumerator.Enumerate(_configService.ConfigHandle);
        if (binds.FirstOrDefault(b =>
                b.Action == item.RawAction && TriggerLabeler.Describe(b) == item.Label)
            is not { } match) return;

        TryWriteKeybinds(current => op(current, match));
    }

    /// <summary>
    /// Read the user's keybind lines, apply <paramref name="transform"/>, write
    /// them back, and reload. The read/write/reload touch disk, so the whole
    /// sequence is guarded: an IOException/UnauthorizedAccessException here would
    /// otherwise fail-fast the process (this runs from an async-void handler).
    /// On failure the edit is logged and dropped, leaving the file unchanged.
    /// </summary>
    private void TryWriteKeybinds(Func<string[], string[]> transform)
    {
        try
        {
            var current = _editor.GetRepeatableValues("keybind");
            var updated = transform(current);
            _editor.SetRepeatableValues("keybind", updated);
            _configService.Reload(); // raises ConfigChanged -> Rebuild
        }
        catch (System.Exception ex)
        {
            StaticLoggers.KeybindingsPage.LogKeybindWriteFailed(ex);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    private void ConflictsToggle_Toggled(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
        => BindingsList.ItemsSource = _catalog.Filter(SearchBox.Text, ConflictsToggle.IsChecked == true);

    private void ViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var keyboard = sender.SelectedItem == KeyboardBarItem;
        KeyboardPanel.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
        if (keyboard) ApplyMap();
    }

    private async void Map_KeyClicked(object? sender, Ghostty.Settings.KeyboardKeyClickedEventArgs e)
    {
        var mask = Map.ModifierMask;

        // Chords aren't representable on a single key, so route a multi-step
        // binding to the full capture dialog instead of the inline picker.
        if (e.State is { IsMultiStep: true } ms)
        {
            await RebindActionAsync(ms.RawAction, ms.ActionLabel);
            return;
        }

        // Dark key (nothing bound on this layer): pick an action and assign it
        // to this physical trigger.
        if (e.State is null)
        {
            var action = await PickActionAsync(preselect: null);
            if (action is null) return;
            var token = KeybindTriggerSyntax.EncodePhysical(mask, e.Cell.Ordinal);
            TryWriteKeybinds(cur => UserKeybindEditor.Assign(cur, token, action));
            return;
        }

        // Lit key (single-step): offer reassign / unbind / reset.
        ShowKeyFlyout(e.Anchor, e.Cell, mask, e.State);
    }

    private void ShowKeyFlyout(FrameworkElement anchor, KeyCell cell, uint mask, KeyboardKeyState state)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem { Text = state.ActionLabel, IsEnabled = false });
        flyout.Items.Add(new MenuFlyoutSeparator());

        var reassign = new MenuFlyoutItem { Text = "Reassign..." };
        reassign.Click += async (_, _) =>
        {
            var action = await PickActionAsync(preselect: state.RawAction);
            if (action is null) return;
            var token = KeybindTriggerSyntax.EncodePhysical(mask, cell.Ordinal);
            TryWriteKeybinds(cur => UserKeybindEditor.Assign(cur, token, action));
        };
        flyout.Items.Add(reassign);

        var unbind = new MenuFlyoutItem { Text = "Unbind" };
        unbind.Click += (_, _) => TryWriteKeybinds(cur => UserKeybindEditor.Unbind(cur, state.Bind));
        flyout.Items.Add(unbind);

        if (state.Source == KeybindSource.User)
        {
            var reset = new MenuFlyoutItem { Text = "Reset to default" };
            reset.Click += (_, _) => TryWriteKeybinds(cur => UserKeybindEditor.Reset(cur, state.Bind));
            flyout.Items.Add(reset);
        }

        flyout.ShowAt(anchor);
    }

    private async Task<string?> PickActionAsync(string? preselect)
    {
        var dialog = new Ghostty.Settings.AssignActionDialog(_binds, preselect) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.SelectedAction : null;
    }

    private async Task RebindActionAsync(string rawAction, string friendly)
    {
        var dialog = new Ghostty.Settings.RebindDialog(_binds, rawAction, friendly) { XamlRoot = XamlRoot };
        var dr = await dialog.ShowAsync();
        if (dr != ContentDialogResult.Primary || dialog.CapturedTrigger is not { } token) return;
        TryWriteKeybinds(cur => UserKeybindEditor.Assign(cur, token, rawAction));
    }
}

internal static partial class KeybindingsPageLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SettingsUi.KeybindWriteFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to write keybind config")]
    internal static partial void LogKeybindWriteFailed(
        this ILogger<KeybindingsPage> logger, System.Exception ex);
}
