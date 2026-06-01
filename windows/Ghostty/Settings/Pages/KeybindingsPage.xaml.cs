using System;
using Ghostty.Core.Input;
using Ghostty.Interop;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private KeybindCatalog _catalog;

    public KeybindingsPage(ConfigService configService)
    {
        _configService = configService;
        InitializeComponent();

        _catalog = KeybindCatalog.Build(Array.Empty<EnumeratedKeybind>());
        BindingsList.ContainerContentChanging += OnContainerContentChanging;
        Rebuild();

        _configService.ConfigChanged += OnConfigChanged;
        Unloaded += OnUnloaded;
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
        _catalog = KeybindCatalog.Build(binds);
        BindingsList.ItemsSource = _catalog.Flatten();
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
                break;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        BindingsList.ItemsSource = _catalog.Filter(sender.Text);
    }
}
