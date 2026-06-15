using System;
using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Input;

namespace Ghostty.Commands;

internal sealed class BuiltInCommandSource : ICommandSource
{
    private readonly Func<PaneAction, Action<CommandItem>> _paneActionFactory;
    private readonly Func<string, Action<CommandItem>> _bindingActionFactory;
    private readonly Action<int>? _opacityAction;
    private readonly Func<bool>? _canUndo;
    private readonly Func<bool>? _canRedo;
    private List<CommandItem>? _cache;

    public BuiltInCommandSource(
        Func<PaneAction, Action<CommandItem>> paneActionFactory,
        Func<string, Action<CommandItem>> bindingActionFactory,
        Action<int>? opacityAction = null,
        Func<bool>? canUndo = null,
        Func<bool>? canRedo = null)
    {
        _paneActionFactory = paneActionFactory;
        _bindingActionFactory = bindingActionFactory;
        _opacityAction = opacityAction;
        _canUndo = canUndo;
        _canRedo = canRedo;
    }

    public IReadOnlyList<CommandItem> GetCommands()
    {
        _cache ??= BuildCommands();
        return _cache;
    }

    public void Refresh() => _cache = null;

    private List<CommandItem> BuildCommands()
    {
        var commands = new List<CommandItem>();

        // PaneAction-backed commands
        AddPaneCommand(commands, PaneAction.SplitVertical, "Split Vertically", "Split the current pane vertically", CommandCategory.Pane, "\uF57E");
        AddPaneCommand(commands, PaneAction.SplitHorizontal, "Split Horizontally", "Split the current pane horizontally", CommandCategory.Pane, "\uF57E");
        AddPaneCommand(commands, PaneAction.NewTab, "New Tab", "Open a new terminal tab", CommandCategory.Tab, "\uE710");
        AddPaneCommand(commands, PaneAction.CloseActiveProgressive, "Close Pane / Tab", "Close active pane; if last pane, close tab; if last tab, close window", CommandCategory.Tab, "\uE711");
        AddPaneCommand(commands, PaneAction.ReopenClosedTab, "Reopen Closed Tab", "Reopen the most recently closed tab", CommandCategory.Tab, "\uE7A7");
        AddPaneCommand(commands, PaneAction.ReopenClosedWindow, "Reopen Closed Window", "Reopen the most recently closed window", CommandCategory.Tab, "\uE8A7");
        AddPaneCommand(commands, PaneAction.NextTab, "Next Tab", "Switch to the next tab", CommandCategory.Tab, "\uE76C");
        AddPaneCommand(commands, PaneAction.PrevTab, "Previous Tab", "Switch to the previous tab", CommandCategory.Tab, "\uE76B");
        AddPaneCommand(commands, PaneAction.ShowTabOverview, "Show all tabs", "Open a grid overview of every tab", CommandCategory.Tab, "\uE8A9");
        AddPaneCommand(commands, PaneAction.FocusLeft, "Focus Pane Left", "Move focus to the left pane", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.FocusRight, "Focus Pane Right", "Move focus to the right pane", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.FocusUp, "Focus Pane Up", "Move focus to the pane above", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.FocusDown, "Focus Pane Down", "Move focus to the pane below", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.GotoSplitPrevious, "Focus Previous Pane", "Move focus to the previous pane in tree order", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.GotoSplitNext, "Focus Next Pane", "Move focus to the next pane in tree order", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.ResizeSplitUp, "Resize Split Up", "Move the nearest horizontal split divider up", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.ResizeSplitDown, "Resize Split Down", "Move the nearest horizontal split divider down", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.ResizeSplitLeft, "Resize Split Left", "Move the nearest vertical split divider left", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.ResizeSplitRight, "Resize Split Right", "Move the nearest vertical split divider right", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.MoveTabRight, "Move Tab Right", "Move the active tab one position right", CommandCategory.Tab);
        AddPaneCommand(commands, PaneAction.MoveTabLeft, "Move Tab Left", "Move the active tab one position left", CommandCategory.Tab);
        AddPaneCommand(commands, PaneAction.ToggleVerticalTabsPinned, "Toggle Vertical Tabs Pinned", "Pin or unpin the vertical tab sidebar", CommandCategory.Tab);
        AddPaneCommand(commands, PaneAction.ToggleTabLayout, "Toggle Tab Layout", "Switch between horizontal and vertical tab layout", CommandCategory.Tab, "\uE8AB");
        AddPaneCommand(commands, PaneAction.ToggleQuickTerminal, "Toggle Quake Terminal", "Show or hide the singleton drop-down terminal", CommandCategory.Terminal);
        AddPaneCommand(commands, PaneAction.ShowKeybindCheatsheet, "Keyboard Shortcuts", "Show the keyboard shortcuts cheat sheet", CommandCategory.Terminal, "");
        AddPaneCommand(commands, PaneAction.ShowAbout, "About", "Show app version, links, and license", CommandCategory.About, "");
        // Undo/Redo are omitted when their stack is empty so the palette
        // never offers a dead no-op. The palette rebuilds this list on every
        // open (CommandPaletteViewModel.Open -> Refresh + GetCommands), so a
        // build-time check reflects the active pane's current history; a null
        // predicate (no host wired) keeps the legacy always-shown behavior.
        if (_canUndo?.Invoke() ?? true)
            AddPaneCommand(commands, PaneAction.Undo, "Undo", "Undo the last pane split, close, resize, equalize, or zoom", CommandCategory.Pane);
        if (_canRedo?.Invoke() ?? true)
            AddPaneCommand(commands, PaneAction.Redo, "Redo", "Redo the last undone pane operation", CommandCategory.Pane);

        // Binding-action-backed commands
        AddBindingCommand(commands, "reset", "Reset Terminal", "Reset the terminal to a clean state", CommandCategory.Terminal, "\uE777");
        AddBindingCommand(commands, "copy_to_clipboard", "Copy to Clipboard", "Copy the current selection to clipboard", CommandCategory.Terminal, "\uE8C8");
        AddBindingCommand(commands, "paste_from_clipboard", "Paste from Clipboard", "Paste clipboard contents into terminal", CommandCategory.Terminal, "\uE77F");
        AddBindingCommand(commands, "select_all", "Select All", "Select all terminal content", CommandCategory.Terminal);
        AddBindingCommand(commands, "increase_font_size:1", "Increase Font Size", "Make terminal text larger", CommandCategory.Terminal, "\uE8E8");
        AddBindingCommand(commands, "decrease_font_size:1", "Decrease Font Size", "Make terminal text smaller", CommandCategory.Terminal, "\uE71F");
        AddBindingCommand(commands, "reset_font_size", "Reset Font Size", "Reset font size to default", CommandCategory.Terminal);
        AddBindingCommand(commands, "clear_screen", "Clear Screen", "Clear the terminal screen and scrollback", CommandCategory.Terminal, "\uE894");
        AddBindingCommand(commands, "scroll_to_top", "Scroll to Top", "Jump to the top of scrollback", CommandCategory.Terminal);
        AddBindingCommand(commands, "scroll_to_bottom", "Scroll to Bottom", "Jump to the bottom of scrollback", CommandCategory.Terminal);
        AddBindingCommand(commands, "jump_to_prompt:-1", "Jump to Previous Prompt", "Scroll to the previous shell prompt (requires OSC 133)", CommandCategory.Terminal);
        AddBindingCommand(commands, "jump_to_prompt:1", "Jump to Next Prompt", "Scroll to the next shell prompt (requires OSC 133)", CommandCategory.Terminal);
        AddBindingCommand(commands, "open_config", "Open Config", "Open the Ghostty configuration file", CommandCategory.Config, "\uE713");
        AddBindingCommand(commands, "reload_config", "Reload Config", "Reload configuration from disk", CommandCategory.Config, "\uE72C");
        AddBindingCommand(commands, "inspector:toggle", "Toggle Inspector", "Show or hide the terminal inspector", CommandCategory.Terminal, "\uEBE8");

        // Opacity adjustment (Ctrl+Shift+Scroll, or from palette)
        if (_opacityAction is not null)
        {
            var adjust = _opacityAction;
            commands.Add(new CommandItem
            {
                Id = "shell:increase_opacity",
                Title = "Increase Opacity",
                Description = "Increase background opacity by 5%",
                Subtitle = "Ctrl+Shift+Scroll Up",
                Category = CommandCategory.Terminal,
                LeadingIcon = "\uE706",
                Execute = _ => adjust(1),
            });
            commands.Add(new CommandItem
            {
                Id = "shell:decrease_opacity",
                Title = "Decrease Opacity",
                Description = "Decrease background opacity by 5%",
                Subtitle = "Ctrl+Shift+Scroll Down",
                Category = CommandCategory.Terminal,
                LeadingIcon = "\uE708",
                Execute = _ => adjust(-1),
            });
            commands.Add(new CommandItem
            {
                Id = "shell:reset_opacity",
                Title = "Reset Opacity",
                Description = "Reset background opacity to 100%",
                Category = CommandCategory.Terminal,
                LeadingIcon = "\uE7A5",
                Execute = _ => adjust(0),
            });
        }

        return commands;
    }

    private void AddPaneCommand(List<CommandItem> list, PaneAction action, string title,
        string description, CommandCategory category, string? icon = null)
    {
        var shortcut = FindShortcut(action);
        list.Add(new CommandItem
        {
            Id = $"pane:{action}",
            Title = title,
            Description = description,
            ActionKey = action.ToString().ToLowerInvariant(),
            Category = category,
            LeadingIcon = icon,
            Shortcut = shortcut,
            Execute = _paneActionFactory(action),
        });
    }

    private void AddBindingCommand(List<CommandItem> list, string actionKey, string title,
        string description, CommandCategory category, string? icon = null)
    {
        list.Add(new CommandItem
        {
            Id = $"binding:{actionKey}",
            Title = title,
            Description = description,
            ActionKey = actionKey,
            Category = category,
            LeadingIcon = icon,
            Execute = _bindingActionFactory(actionKey),
        });
    }

    private static KeyBinding? FindShortcut(PaneAction action) =>
        KeyBindings.WindowsOnly.Find(action);
}
