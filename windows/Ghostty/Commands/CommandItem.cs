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
    public bool Emphasis { get; init; }
    public string? Badge { get; init; }
    public string? PreviewText { get; init; }
    public required Action<CommandItem> Execute { get; init; }
}
