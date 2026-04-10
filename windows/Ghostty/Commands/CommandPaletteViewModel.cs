using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ghostty.Commands;

internal enum PaletteMode
{
    Search,
    CommandLine,
}

// Suppress MVVMTK0045: this ViewModel is internal and accessed from code-behind,
// never directly x:Bind'd from XAML, so the WinRT-marshalling AOT concern does not apply.
#pragma warning disable MVVMTK0045
internal partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<ICommandSource> _sources;
    private readonly FrecencyStore _frecency;
    private readonly ActionAutoCompleter? _autoCompleter;
    private readonly bool _groupByCategory;
    private List<CommandItem> _allCommands = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private PaletteMode _mode = PaletteMode.Search;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private CommandItem? _selectedCommand;

    [ObservableProperty]
    private List<CommandItem> _filteredCommands = [];

    [ObservableProperty]
    private string? _ghostText;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _modeLabel = "Search";

    public CommandPaletteViewModel(
        IReadOnlyList<ICommandSource> sources,
        FrecencyStore frecency,
        ActionAutoCompleter? autoCompleter,
        bool groupByCategory = false)
    {
        _sources = sources;
        _frecency = frecency;
        _autoCompleter = autoCompleter;
        _groupByCategory = groupByCategory;
    }

    public void Open()
    {
        foreach (var source in _sources)
            source.Refresh();

        _allCommands = _sources.SelectMany(s => s.GetCommands()).ToList();

        IsOpen = true;
        SearchText = "";
        IsPinned = false;
        Mode = PaletteMode.Search;
        ApplyFilter();
    }

    public void Close()
    {
        IsOpen = false;
        SearchText = "";
        IsPinned = false;
        Mode = PaletteMode.Search;
        FilteredCommands = [];
        GhostText = null;
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    [RelayCommand]
    public void ExecuteSelectedCommand()
    {
        if (SelectedCommand is null) return;

        _frecency.RecordUse(SelectedCommand.Id);
        SelectedCommand.Execute(SelectedCommand);

        if (IsPinned)
        {
            SearchText = "";
            ApplyFilter();
        }
        else
        {
            Close();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (value.StartsWith('>'))
        {
            Mode = PaletteMode.CommandLine;
            ModeLabel = "Command";
            ApplyCommandLineFilter(value[1..].TrimStart());
        }
        else
        {
            Mode = PaletteMode.Search;
            ModeLabel = "Search";
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText;
        IEnumerable<CommandItem> filtered;

        if (string.IsNullOrEmpty(query))
        {
            filtered = _allCommands;
        }
        else
        {
            filtered = _allCommands.Where(c =>
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (c.ActionKey?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredCommands = _groupByCategory
            ? filtered
                .OrderBy(c => c.Category)
                .ThenByDescending(c => _frecency.Score(c.Id))
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : filtered
                .OrderByDescending(c => _frecency.Score(c.Id))
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

        GhostText = null;
        StatusText = FilteredCommands.Count == 1
            ? "1 command"
            : $"{FilteredCommands.Count} commands";

        if (FilteredCommands.Count > 0)
            SelectedCommand = FilteredCommands[0];
    }

    private void ApplyCommandLineFilter(string input)
    {
        if (_autoCompleter is null)
        {
            FilteredCommands = [];
            GhostText = null;
            StatusText = "No autocomplete available";
            return;
        }

        var result = _autoCompleter.Complete(input);

        FilteredCommands = result.Suggestions
            .Select(s => new CommandItem
            {
                Id = $"cmdline:{s.Name}",
                Title = s.Name,
                Description = s.Description,
                ActionKey = s.Name,
                Category = CommandCategory.Custom,
                Execute = _ => { },
            })
            .ToList();

        GhostText = result.GhostText;
        StatusText = FilteredCommands.Count == 1
            ? "1 action"
            : $"{FilteredCommands.Count} actions";

        if (FilteredCommands.Count > 0)
            SelectedCommand = FilteredCommands[0];
    }

    public void AcceptAutocomplete()
    {
        if (Mode != PaletteMode.CommandLine || GhostText is null) return;
        SearchText += GhostText;
    }

    public void MoveSelectionUp()
    {
        if (FilteredCommands.Count == 0) return;
        var idx = SelectedCommand is not null ? FilteredCommands.IndexOf(SelectedCommand) : 0;
        idx = Math.Max(0, idx - 1);
        SelectedCommand = FilteredCommands[idx];
    }

    public void MoveSelectionDown()
    {
        if (FilteredCommands.Count == 0) return;
        var idx = SelectedCommand is not null ? FilteredCommands.IndexOf(SelectedCommand) : -1;
        idx = Math.Min(FilteredCommands.Count - 1, idx + 1);
        SelectedCommand = FilteredCommands[idx];
    }
}
#pragma warning restore MVVMTK0045
