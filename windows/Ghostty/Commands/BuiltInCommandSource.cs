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
    private List<CommandItem>? _cache;

    public BuiltInCommandSource(
        Func<PaneAction, Action<CommandItem>> paneActionFactory,
        Func<string, Action<CommandItem>> bindingActionFactory,
        Action<int>? opacityAction = null)
    {
        _paneActionFactory = paneActionFactory;
        _bindingActionFactory = bindingActionFactory;
        _opacityAction = opacityAction;
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
        AddPaneCommand(commands, PaneAction.NextTab, "Next Tab", "Switch to the next tab", CommandCategory.Tab, "\uE76C");
        AddPaneCommand(commands, PaneAction.PrevTab, "Previous Tab", "Switch to the previous tab", CommandCategory.Tab, "\uE76B");
        AddPaneCommand(commands, PaneAction.FocusLeft, "Focus Pane Left", "Move focus to the left pane", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.FocusRight, "Focus Pane Right", "Move focus to the right pane", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.FocusUp, "Focus Pane Up", "Move focus to the pane above", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.FocusDown, "Focus Pane Down", "Move focus to the pane below", CommandCategory.Pane);
        AddPaneCommand(commands, PaneAction.MoveTabRight, "Move Tab Right", "Move the active tab one position right", CommandCategory.Tab);
        AddPaneCommand(commands, PaneAction.MoveTabLeft, "Move Tab Left", "Move the active tab one position left", CommandCategory.Tab);
        AddPaneCommand(commands, PaneAction.ToggleVerticalTabsPinned, "Toggle Vertical Tabs Pinned", "Pin or unpin the vertical tab sidebar", CommandCategory.Tab);
        AddPaneCommand(commands, PaneAction.ToggleTabLayout, "Toggle Tab Layout", "Switch between horizontal and vertical tab layout", CommandCategory.Tab, "\uE8AB");

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
        AddBindingCommand(commands, "open_config", "Open Config", "Open the Ghostty configuration file", CommandCategory.Config, "\uE713");
        AddBindingCommand(commands, "reload_config", "Reload Config", "Reload configuration from disk", CommandCategory.Config, "\uE72C");

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
        KeyBindings.Default.Find(action);
}
