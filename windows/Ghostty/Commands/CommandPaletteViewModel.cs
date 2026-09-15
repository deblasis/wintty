using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Ghostty.Core.Commands;
using Ghostty.Core.Themes;

namespace Ghostty.Commands;

internal enum PaletteMode
{
    Search,
    CommandLine,
    // The theme list: the highlight previews on the live views, Enter keeps
    // it, Escape (or any other close) puts back what was there.
    Theme,
}

/// <summary>
/// What the palette's theme list needs from its window: the themes to offer,
/// the one on screen when the list opens, and the browse that previews,
/// reverts and commits. Null on the view model means the palette has no
/// theme list.
/// </summary>
internal sealed class PaletteThemeMode
{
    public required Func<IReadOnlyList<string>> Themes { get; init; }
    public required Func<string?> ActiveTheme { get; init; }
    public required PaletteThemeBrowse Browse { get; init; }

    /// <summary>The rows' swatch colours, read on first use and dropped per browse.</summary>
    public required ThemeSwatchCache Swatches { get; init; }

    /// <summary>
    /// Typing refilters the list once it pauses, so the preview that follows
    /// the highlight is asked for once per word, not once per keystroke.
    /// </summary>
    public required PaletteFilterDebounce Filter { get; init; }
}

/// <summary>
/// Backs the command palette control. Pure code-behind binding
/// (see <c>CommandPaletteControl.Bind</c>), so the type stays internal.
/// INPC is hand-rolled with the C# 14 <c>field</c> keyword for the
/// same reason as <c>TabModel</c>: no source generator dependency.
/// </summary>
internal partial class CommandPaletteViewModel : INotifyPropertyChanged
{
    /// <summary>Id prefix of a theme row, followed by the theme's name.</summary>
    private const string ThemeItemPrefix = "theme-item:";

    private readonly IReadOnlyList<ICommandSource> _sources;
    private readonly FrecencyStore _frecency;
    private readonly ActionAutoCompleter? _autoCompleter;
    private readonly bool _groupByCategory;
    private readonly Action<string>? _commandLineDispatch;
    private readonly PaletteThemeMode? _themeMode;
    private List<CommandItem> _allCommands = [];
    private IReadOnlyList<string> _themes = [];
    private string? _activeTheme;

    // True while the theme list places its own initial highlight, which is
    // where the user already is: that placement is not a browse, and must
    // not preview (or snapshot) anything.
    private bool _placingThemeSelection;

    public bool IsOpen
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    }

    public string SearchText
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise();
            OnSearchTextChanged(value);
        }
    } = "";

    public PaletteMode Mode
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    } = PaletteMode.Search;

    public bool IsPinned
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    }

    public CommandItem? SelectedCommand
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise();
            // In the theme list, moving the highlight IS the preview. A null
            // (a filter that matched nothing) leaves the preview where it is.
            if (Mode == PaletteMode.Theme && !_placingThemeSelection)
                _themeMode?.Browse.Select(ThemeNameOf(value));
        }
    }

    public List<CommandItem> FilteredCommands
    {
        // Always a fresh list from ApplyFilter/ApplyCommandLineFilter,
        // so no reference-equality guard: it would always pass anyway.
        get;
        set { field = value; Raise(); }
    } = [];

    public string? GhostText
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    }

    public string StatusText
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    } = "";

    public string ModeLabel
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    } = "Search";

    /// <summary>The highlighted theme while the theme list is up, else null.</summary>
    public string? SelectedThemeName => Mode == PaletteMode.Theme ? ThemeNameOf(SelectedCommand) : null;

    /// <summary>
    /// The line the theme list shows in place of rows when the filter matched
    /// nothing, else null.
    /// </summary>
    public string? ThemeNoMatchText
    {
        get;
        set { if (field != value) { field = value; Raise(); } }
    }

    /// <summary>Whether typing has changed the theme filter and it has not applied yet.</summary>
    internal bool IsThemeFilterPending => Mode == PaletteMode.Theme && _themeMode?.Filter.IsPending == true;

    /// <summary>How many themes the theme list holds before filtering, else 0.</summary>
    internal int ThemeCount => Mode == PaletteMode.Theme ? _themes.Count : 0;

    /// <summary>How many previews the current theme browse has applied, else 0.</summary>
    internal int ThemePreviewApplies => Mode == PaletteMode.Theme ? _themeMode?.Browse.ApplyCount ?? 0 : 0;

    public CommandPaletteViewModel(
        IReadOnlyList<ICommandSource> sources,
        FrecencyStore frecency,
        ActionAutoCompleter? autoCompleter,
        bool groupByCategory = false,
        Action<string>? commandLineDispatch = null,
        PaletteThemeMode? themeMode = null)
    {
        _sources = sources;
        _frecency = frecency;
        _autoCompleter = autoCompleter;
        _groupByCategory = groupByCategory;
        _commandLineDispatch = commandLineDispatch;
        _themeMode = themeMode;
    }

    public void Open()
    {
        RebuildCommands();

        IsOpen = true;
        SearchText = "";
        IsPinned = false;
        Mode = PaletteMode.Search;
        ApplyFilter();
    }

    // Refresh every source and re-collect the command list. Sources can
    // emit context-dependent commands (e.g. BuiltInCommandSource omits
    // Undo/Redo when the active pane's stack is empty), so this must run
    // any time the list could have gone stale -- on open AND after a
    // command executes while the palette stays pinned open.
    private void RebuildCommands()
    {
        foreach (var source in _sources)
            source.Refresh();

        _allCommands = _sources.SelectMany(s => s.GetCommands()).ToList();
    }

#if DEMO
    /// <summary>
    /// Find a command by its <see cref="CommandItem.Id"/> across all sources and
    /// run it without opening the palette UI. Returns false if no command matches
    /// (e.g. a Pro-only command id in an OSS build). Used by the demo "command"
    /// beat to fire palette-only commands that have no PaneAction.
    /// </summary>
    public bool TryExecuteById(string id)
    {
        // Query a fresh local list rather than RebuildCommands(): that would
        // overwrite the live _allCommands out from under an open/pinned palette.
        // Sources were already refreshed when the demo built them.
        var cmd = _sources.SelectMany(s => s.GetCommands()).FirstOrDefault(c => c.Id == id);
        if (cmd is null) return false;
        cmd.Execute(cmd);
        return true;
    }
#endif

    public void Close()
    {
        // Every way the palette goes away funnels through here: Escape, the
        // toggle chord, a click outside, the window closing. So this is where
        // an unconfirmed theme browse is undone -- and it has to happen
        // before the resets below, which would otherwise rebuild a theme
        // list for a palette that is closing.
        if (Mode == PaletteMode.Theme)
        {
            _themeMode?.Browse.Cancel();
            LeaveThemeMode();
        }

        IsOpen = false;
        SearchText = "";
        IsPinned = false;
        Mode = PaletteMode.Search;
        FilteredCommands = [];
        SelectedCommand = PaletteSelection.SelectTop(FilteredCommands);
        GhostText = null;
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void ExecuteSelectedCommand()
    {
        if (Mode == PaletteMode.Theme)
        {
            ConfirmSelectedTheme();
            return;
        }

        if (SelectedCommand is null) return;

        _frecency.RecordUse(SelectedCommand.Id);
        SelectedCommand.Execute(SelectedCommand);

        // The command turned this palette into its theme list, in place.
        // Closing now would throw the list away before anyone saw it.
        if (Mode == PaletteMode.Theme) return;

        if (IsPinned)
        {
            // Rebuild so context commands (e.g. Undo/Redo visibility) don't
            // freeze at their open-time state while the palette stays pinned.
            // Pane actions dispatch on a later tick, so a history op fired
            // from here is reflected on the next execute/reopen, not this one
            // -- acceptable for a cosmetic availability hint.
            RebuildCommands();
            SearchText = "";
            ApplyFilter();
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// Replace the command list with the theme list, highlighting the theme
    /// on screen. Nothing is previewed until the highlight moves.
    /// </summary>
    public void EnterThemeMode()
    {
        if (_themeMode is null || !IsOpen || Mode == PaletteMode.Theme) return;

        _themes = _themeMode.Themes();
        _activeTheme = _themeMode.ActiveTheme();
        // Theme files can change between browses; within one they are read once.
        _themeMode.Swatches.Clear();
        _themeMode.Browse.Begin();

        _placingThemeSelection = true;
        try
        {
            Mode = PaletteMode.Theme;
            ModeLabel = "Theme";
            GhostText = null;
            SearchText = "";
            ApplyThemeFilter(initial: true);
            // The reset above is a text change in theme mode, so it asked for
            // a refilter; the initial filter just applied is that refilter.
            // Left waiting, it would land a moment later and move the
            // highlight off the theme on screen to the top row, and preview it.
            _themeMode.Filter.Cancel();
        }
        finally
        {
            _placingThemeSelection = false;
        }
    }

    private void ConfirmSelectedTheme()
    {
        // Enter keeps what the typed text describes, even when it was pressed
        // before typing paused.
        FlushThemeFilter();

        // Enter on an empty filter has nothing to keep; the list stays up.
        if (ThemeNameOf(SelectedCommand) is not { } chosen) return;

        _themeMode?.Browse.Confirm(chosen);
        LeaveThemeMode();
        Close();
    }

    private void LeaveThemeMode()
    {
        // A filter still waiting for typing to pause must not land on the
        // command list that comes back.
        _themeMode?.Filter.Cancel();
        ThemeNoMatchText = null;
        Mode = PaletteMode.Search;
        ModeLabel = "Search";
        _themes = [];
        _activeTheme = null;
    }

    private void OnSearchTextChanged(string value)
    {
        if (Mode == PaletteMode.Theme)
        {
            // Typing filters the theme list once it pauses; the highlight
            // then lands on the best match, which previews it like any other
            // move. Per keystroke, the preview would show a theme for every
            // prefix of the name on the way to the one being typed.
            if (_themeMode is null) ApplyThemeFilter(initial: false);
            else _themeMode.Filter.Request(() => ApplyThemeFilter(initial: false));
            return;
        }

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

    private void ApplyThemeFilter(bool initial)
    {
        var names = ThemeCatalog.Filter(_themes, SearchText);
        FilteredCommands = names.Select(ThemeItem).ToList();
        GhostText = null;
        // The status line is the palette's live region, so "no match" is
        // spoken as well as shown in the empty list.
        StatusText = _themes.Count == 0
            ? "No themes found"
            : names.Count == 0 ? "No themes match"
            : names.Count == 1 ? "1 theme" : $"{names.Count} themes";
        ThemeNoMatchText = _themes.Count > 0 && names.Count == 0
            ? $"No themes match “{SearchText.Trim()}”"
            : null;

        CommandItem? highlight = null;
        if (initial && ThemeCatalog.InitialSelection(names, _activeTheme) is { } active)
            highlight = FilteredCommands.FirstOrDefault(c => ThemeNameOf(c) == active);

        // Set for the empty case too, for the reason ApplyFilter gives: a
        // stale selection behind a filter that matched nothing would be what
        // Enter keeps.
        SelectedCommand = highlight ?? PaletteSelection.SelectTop(FilteredCommands);
    }

    private CommandItem ThemeItem(string name) => new()
    {
        Id = ThemeItemPrefix + name,
        Title = name,
        // The row draws its own "Current" badge and says "current theme" in
        // its accessible name (ThemeRowPresentation), so no description.
        Description = "",
        ThemeName = name,
        IsCurrentTheme = string.Equals(name, _activeTheme, StringComparison.OrdinalIgnoreCase),
        Category = CommandCategory.Config,
        // Enter on a theme row is handled by the view model (it confirms the
        // browse), so the row itself does nothing when executed.
        Execute = static _ => { },
    };

    /// <summary>
    /// The swatch for a theme row if it has been read already, without
    /// reading anything: what a row realized during a filter keystroke or a
    /// scroll is painted from, synchronously, so it never blinks.
    /// </summary>
    internal bool TryGetCachedThemeSwatch(string themeName, out ThemeSwatch? swatch)
    {
        swatch = null;
        return _themeMode is not null && _themeMode.Swatches.TryGetCached(themeName, out swatch);
    }

    /// <summary>The swatch for a theme row, reading its file the first time.</summary>
    internal ThemeSwatch? LoadThemeSwatch(string themeName) => _themeMode?.Swatches.Get(themeName);

    /// <summary>
    /// Whether the browse is showing, or about to show, this theme. False for
    /// the highlight a fresh list opens on, which previews nothing.
    /// </summary>
    internal bool IsThemePreviewed(string themeName)
        => Mode == PaletteMode.Theme
           && string.Equals(_themeMode?.Browse.TargetTheme, themeName, StringComparison.Ordinal);

    private static string? ThemeNameOf(CommandItem? item)
        => item is not null && item.Id.StartsWith(ThemeItemPrefix, StringComparison.Ordinal)
            ? item.Id[ThemeItemPrefix.Length..]
            : null;

    private void ApplyFilter()
    {
        var query = SearchText;
        IEnumerable<CommandItem> filtered;

        if (string.IsNullOrEmpty(query))
        {
            // Debug rows are absent from the unfiltered list, not merely last
            // in it. Sorting them last governs position, and title-only
            // matching needs a query to apply, so both mitigations miss this
            // branch by construction: open the palette, press End, press
            // Enter, and the window goes down with whatever was in the
            // terminal, on a shipped build, with no confirmation.
            //
            // The invariant is that a destructive row has to be asked for by
            // name. Excluding them here is what makes it true. Typing any part
            // of the title still finds them, and `+crash` is untouched.
            filtered = _allCommands.Where(c => c.Category != CommandCategory.Debug);
        }
        else
        {
            // Debug rows match on their title only. Their descriptions
            // explain what they destroy, in ordinary words, so matching those
            // puts "crash the renderer" in front of someone who typed
            // "select" and meant Select All. A destructive row has to be
            // asked for by name.
            filtered = _allCommands.Where(c =>
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.Category != CommandCategory.Debug && (
                    c.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (c.ActionKey?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))));
        }

        // Debug sorts last in BOTH branches. Category ordering used to apply
        // only when grouping was on, and grouping defaults off, so the one
        // thing keeping a crash row away from the top was its title's
        // alphabetical luck. Frecency cannot be the first key for these
        // either: executing one records the use before it takes the process
        // down, so a single accident promotes that row for every later
        // launch.
        FilteredCommands = _groupByCategory
            ? filtered
                .OrderBy(c => c.Category)
                .ThenByDescending(c => _frecency.Score(c.Id))
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : filtered
                .OrderBy(c => c.Category == CommandCategory.Debug ? 1 : 0)
                .ThenByDescending(c => _frecency.Score(c.Id))
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

        GhostText = null;
        StatusText = FilteredCommands.Count == 1
            ? "1 command"
            : $"{FilteredCommands.Count} commands";

        // Assigned for the empty case too. Assigning only when the list came
        // back non-empty leaves the previous selection live behind a query
        // that matched nothing, and ExecuteSelectedCommand guards only
        // against null - so Enter runs a command that is not on screen.
        SelectedCommand = PaletteSelection.SelectTop(FilteredCommands);
    }

    private void ApplyCommandLineFilter(string input)
    {
        if (_autoCompleter is null)
        {
            FilteredCommands = [];
            SelectedCommand = PaletteSelection.SelectTop(FilteredCommands);
            GhostText = null;
            StatusText = "No autocomplete available";
            return;
        }

        var result = _autoCompleter.Complete(input);

        FilteredCommands = result.Suggestions
            .Select(s =>
            {
                // Capture the resolved full action string (e.g.
                // "increase_font_size:1", not just the parameter "1") so the
                // command-mode entry dispatches the same way regular binding
                // commands do via BuiltInCommandSource.
                var actionKey = s.ActionKey;
                return new CommandItem
                {
                    // Key frecency off the resolved action, not s.Name: for a
                    // parameterized action s.Name is only the parameter (e.g.
                    // "1"), so distinct actions sharing a param value would
                    // otherwise collide in the frecency store.
                    Id = $"cmdline:{actionKey}",
                    Title = s.Name,
                    Description = s.Description,
                    ActionKey = actionKey,
                    Category = CommandCategory.Custom,
                    Execute = _ => _commandLineDispatch?.Invoke(actionKey),
                };
            })
            .ToList();

        GhostText = result.GhostText;
        StatusText = FilteredCommands.Count == 1
            ? "1 action"
            : $"{FilteredCommands.Count} actions";

        SelectedCommand = PaletteSelection.SelectTop(FilteredCommands);
    }

    public void AcceptAutocomplete()
    {
        if (Mode != PaletteMode.CommandLine || GhostText is null) return;
        SearchText += GhostText;
    }

    // Every mover acts on the list the typed text describes: a filter still
    // waiting for typing to pause is applied first.
    public void MoveSelectionUp()
    {
        FlushThemeFilter();
        SelectedCommand = PaletteSelection.Step(FilteredCommands, SelectedCommand, -1);
    }

    public void MoveSelectionDown()
    {
        FlushThemeFilter();
        SelectedCommand = PaletteSelection.Step(FilteredCommands, SelectedCommand, +1);
    }

    /// <summary>Page Up / Page Down: a screenful at a time, clamped at the ends.</summary>
    public void MoveSelectionBy(int delta)
    {
        FlushThemeFilter();
        SelectedCommand = PaletteSelection.Step(FilteredCommands, SelectedCommand, delta);
    }

    private void FlushThemeFilter()
    {
        if (Mode == PaletteMode.Theme) _themeMode?.Filter.Flush();
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
