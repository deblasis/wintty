using System;
using System.Collections.Generic;

namespace Ghostty.Commands;

/// <summary>
/// The palette entry that turns the palette into its theme list. Executing it
/// does not close the palette: the list replaces the commands in place, and
/// the view model decides what Enter and Escape mean from there.
/// </summary>
internal sealed class ThemeCommandSource : ICommandSource
{
    /// <summary>The entry's id, also what the frecency store keys it by.</summary>
    internal const string ChangeThemeId = "theme:change";

    private readonly IReadOnlyList<CommandItem> _commands;

    /// <param name="enterThemeMode">
    /// Switches the palette to its theme list. Called synchronously from the
    /// execute, not deferred like the other sources: the palette checks right
    /// after executing whether it is now in theme mode, and stays open if so.
    /// </param>
    public ThemeCommandSource(Action enterThemeMode)
    {
        ArgumentNullException.ThrowIfNull(enterThemeMode);
        _commands =
        [
            new CommandItem
            {
                Id = ChangeThemeId,
                Title = "Change Theme",
                Description = "Preview themes on the open terminals; Enter keeps one, Esc puts yours back",
                Category = CommandCategory.Config,
                LeadingIcon = "\uE790", // Segoe Fluent Color glyph
                Execute = _ => enterThemeMode(),
            },
        ];
    }

    public IReadOnlyList<CommandItem> GetCommands() => _commands;

    public void Refresh()
    {
    }
}
