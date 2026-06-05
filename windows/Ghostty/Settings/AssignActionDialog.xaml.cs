using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Input;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings;

internal sealed partial class AssignActionDialog : ContentDialog
{
    private readonly IReadOnlyList<KeybindActionCatalog.AssignableAction> _all;

    /// <summary>Chosen raw action string, or null if cancelled.</summary>
    public string? SelectedAction { get; private set; }

    public AssignActionDialog(IReadOnlyList<EnumeratedKeybind> binds, string? preselectRawAction = null)
    {
        _all = KeybindActionCatalog.AllActions(binds);
        InitializeComponent();
        ActionList.ContainerContentChanging += OnContainerContentChanging;
        Apply(null);

        if (preselectRawAction is not null)
        {
            var match = _all.FirstOrDefault(a => a.RawAction == preselectRawAction);
            if (match.RawAction is not null)
            {
                ActionList.SelectedItem = match;
                ActionList.ScrollIntoView(match);
            }
        }
    }

    private void Apply(string? query)
    {
        var items = string.IsNullOrWhiteSpace(query)
            ? _all
            : _all.Where(a => a.Friendly.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || a.RawAction.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || a.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        ActionList.ItemsSource = items;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is KeybindActionCatalog.AssignableAction a
            && args.ItemContainer.ContentTemplateRoot is StackPanel panel)
        {
            if (panel.FindName("FriendlyText") is TextBlock f) f.Text = a.Friendly;
            if (panel.FindName("CategoryText") is TextBlock c) c.Text = a.Category;
        }
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        Apply(FilterBox.Text);
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedAction = ActionList.SelectedItem is KeybindActionCatalog.AssignableAction a ? a.RawAction : null;
        IsPrimaryButtonEnabled = SelectedAction is not null;
    }
}
