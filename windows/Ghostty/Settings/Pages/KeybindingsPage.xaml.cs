using Ghostty.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class KeybindingsPage : Page
{
    private readonly IKeyBindingsProvider _keybindings;

    public KeybindingsPage(IKeyBindingsProvider keybindings)
    {
        _keybindings = keybindings;
        InitializeComponent();
        BindingsList.ItemsSource = _keybindings.All;
        // AOT-safe: populate TextBlocks from code-behind instead of
        // {Binding} which relies on reflection that NativeAOT trims.
        BindingsList.ContainerContentChanging += OnContainerContentChanging;
    }

    private static void OnContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid grid) return;
        if (args.Item is not BindingEntry entry) return;

        if (grid.FindName("ActionText") is TextBlock action)
            action.Text = entry.Action;
        if (grid.FindName("KeyComboText") is TextBlock combo)
            combo.Text = entry.KeyCombo;
        if (grid.FindName("SourceText") is TextBlock source)
            source.Text = entry.Source;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        BindingsList.ItemsSource = _keybindings.Search(sender.Text);
    }
}
