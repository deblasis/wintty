using System;
using Windows.UI;

namespace Ghostty.Commands;

internal enum CommandCategory
{
    Tab,
    Pane,
    Navigation,
    Terminal,
    Config,
    Custom,
    About,
    Demo,
    // Destructive developer actions. Last so that grouped mode sorts them
    // to the bottom, away from anything a user could hit by accident.
    Debug,
}

internal record CommandItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? Subtitle { get; init; }
    public string? ActionKey { get; init; }
    public Input.KeyBinding? Shortcut { get; init; }
    public CommandCategory Category { get; init; }
    public Color? LeadingColor { get; init; }
    public string? LeadingIcon { get; init; }
    // When set, the palette renders a custom PathIcon for this command instead
    // of the LeadingIcon glyph. Only "quake" is handled today.
    public string? LeadingIconPathKey { get; init; }
    public bool Emphasis { get; init; }
    public string? Badge { get; init; }
    public string? PreviewText { get; init; }
    // Set on the rows of the palette's theme list only: the theme the row
    // stands for, and whether it is the one the config names now. The row
    // template draws a swatch and a badge from these instead of the icon,
    // description and key-cap a command row shows.
    public string? ThemeName { get; init; }
    public bool IsCurrentTheme { get; init; }
    public required Action<CommandItem> Execute { get; init; }
}
