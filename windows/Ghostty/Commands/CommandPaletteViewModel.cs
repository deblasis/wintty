using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ghostty.Commands;

internal enum PaletteMode
{
    Search,
    CommandLine,
}

/// <summary>
/// Backs the command palette control. Pure code-behind binding
/// (see <c>CommandPaletteControl.Bind</c>), so the type stays internal
/// and INPC is hand-rolled for the same reason as <c>TabModel</c>:
/// not worth a NuGet dependency for one consumer.
/// </summary>
internal class CommandPaletteViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<ICommandSource> _sources;
    private readonly FrecencyStore _frecency;
    private readonly ActionAutoCompleter? _autoCompleter;
    private readonly bool _groupByCategory;
    private List<CommandItem> _allCommands = [];

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set { if (_isOpen != value) { _isOpen = value; Raise(); } }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Raise();
            OnSearchTextChanged(value);
        }
    }

    private PaletteMode _mode = PaletteMode.Search;
    public PaletteMode Mode
    {
        get => _mode;
        set { if (_mode != value) { _mode = value; Raise(); } }
    }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; Raise(); } }
    }

    private CommandItem? _selectedCommand;
    public CommandItem? SelectedCommand
    {
        get => _selectedCommand;
        set { if (_selectedCommand != value) { _selectedCommand = value; Raise(); } }
    }

    private List<CommandItem> _filteredCommands = [];
    public List<CommandItem> FilteredCommands
    {
        // Always a fresh list from ApplyFilter/ApplyCommandLineFilter,
        // so no reference-equality guard: it would always pass anyway.
        get => _filteredCommands;
        set { _filteredCommands = value; Raise(); }
    }

    private string? _ghostText;
    public string? GhostText
    {
        get => _ghostText;
        set { if (_ghostText != value) { _ghostText = value; Raise(); } }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; Raise(); } }
    }

    private string _modeLabel = "Search";
    public string ModeLabel
    {
        get => _modeLabel;
        set { if (_modeLabel != value) { _modeLabel = value; Raise(); } }
    }

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

    private void OnSearchTextChanged(string value)
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

    // ── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
