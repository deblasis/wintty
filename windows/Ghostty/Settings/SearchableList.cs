using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings;

/// <summary>
/// Wires an <see cref="AutoSuggestBox"/> to a filterable string list
/// with dropdown-on-focus and case-insensitive search. Keeps the
/// pattern DRY across font and theme pickers.
/// </summary>
internal sealed class SearchableList
{
    private readonly AutoSuggestBox _box;
    private readonly Action<string>? _onChosen;
    private IReadOnlyList<string> _items = Array.Empty<string>();

    public SearchableList(AutoSuggestBox box, Action<string>? onChosen = null)
    {
        _box = box;
        _onChosen = onChosen;
        _box.TextChanged += OnTextChanged;
        _box.SuggestionChosen += OnSuggestionChosen;
        _box.GotFocus += OnGotFocus;
    }

    public void SetItems(IReadOnlyList<string> items)
    {
        _items = items;
    }

    private void OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var query = sender.Text;
        sender.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _items
            : _items.Where(i => i.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        sender.IsSuggestionListOpen = true;
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        // If there's already a value, rotate the list so items start
        // from the current selection. This way arrow-down continues
        // from where the user is instead of jumping to the top.
        var text = _box.Text;
        if (!string.IsNullOrEmpty(text))
        {
            var idx = -1;
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            if (idx >= 0)
            {
                var rotated = new List<string>(_items.Count);
                for (int i = idx; i < _items.Count; i++) rotated.Add(_items[i]);
                for (int i = 0; i < idx; i++) rotated.Add(_items[i]);
                _box.ItemsSource = rotated;
                _box.IsSuggestionListOpen = true;
                return;
            }
        }

        _box.ItemsSource = _items;
        _box.IsSuggestionListOpen = true;
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string chosen)
        {
            sender.Text = chosen;
            _onChosen?.Invoke(chosen);
        }
    }
}
