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
///
/// A highlighted suggestion is a PREVIEW, not a choice. WinUI raises
/// <see cref="AutoSuggestBox.SuggestionChosen"/> for every change of the
/// highlighted item, exactly as it does for a pick, so an arrow keypress
/// and a click are the same event as far as this class can tell. Calling the
/// consumer back from there meant every keypress of a browse wrote the
/// config, and Escape -- which closes the list without raising anything --
/// had nothing to revert because nothing had been recorded as a preview.
///
/// So the consumer's callback fires from <see cref="Commit"/> and from
/// nowhere else, and exactly two things reach it: Enter (the box's
/// <see cref="AutoSuggestBox.QuerySubmitted"/>) and a click, which is the
/// same event arriving with the suggestion list already closed. Browsing
/// leaves the list open -- arrowing is how the user goes on choosing -- so
/// the closed list is what separates the two.
/// </summary>
internal sealed class SearchableList
{
    private readonly AutoSuggestBox _box;
    private readonly Action<string>? _onChosen;
    private IReadOnlyList<string> _items = Array.Empty<string>();

    // What the highlight last moved to, cleared by a commit. Enter commits
    // this rather than the box text when there is one: the box already shows
    // it, and the alternative is committing a half-typed query that names
    // nothing.
    private string? _highlighted;

    public SearchableList(AutoSuggestBox box, Action<string>? onChosen = null)
    {
        _box = box;
        _onChosen = onChosen;
        _box.TextChanged += OnTextChanged;
        _box.SuggestionChosen += OnSuggestionChosen;
        _box.QuerySubmitted += OnQuerySubmitted;
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

    // Fires for every highlighted item, on an arrow key as much as on a
    // click. Mirroring the text is the whole job here: it is the preview the
    // user browses over, and the value the box keeps when the list closes.
    // Committing from here is what made every arrow keypress write the file.
    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string chosen) return;

        _highlighted = chosen;
        sender.Text = chosen;

        // A click closes the suggestion list and browsing leaves it open.
        // WinUI offers no event for either, so that is the whole signal, and
        // it is the one commit this handler makes.
        if (!sender.IsSuggestionListOpen) Commit(chosen);
    }

    // Enter in the box, and the one commit signal that cannot be a highlight.
    // The highlighted item wins when there is one, so arrowing down a list
    // and pressing Enter commits what the highlight was on; a query typed in
    // full and submitted commits the text.
    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => Commit(_highlighted ?? sender.Text.Trim());

    // The one exit every commit goes through, so there is a single policy
    // and a single place a consumer's callback can be reached from.
    //
    // An empty value is dropped rather than committed: the Colors pair reads
    // both boxes and needs both, and committing an empty box writes
    // `theme = ` over whatever the other half said.
    private void Commit(string value)
    {
        _highlighted = null;
        if (string.IsNullOrEmpty(value)) return;
        _onChosen?.Invoke(value);
    }
}
