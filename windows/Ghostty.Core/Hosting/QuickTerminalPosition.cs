namespace Ghostty.Core.Hosting;

/// <summary>
/// Where the quake / drop-down terminal docks on the chosen monitor.
/// Mirrors the upstream `quick-terminal-position` config key
/// (top/bottom/left/right/center).
/// </summary>
internal enum QuickTerminalPosition
{
    Top,
    Bottom,
    Left,
    Right,
    Center,
}

internal static class QuickTerminalPositionExtensions
{
    /// <summary>
    /// Parse a libghostty-formatted enum tag string. Unknown or
    /// null falls back to <see cref="QuickTerminalPosition.Top"/>
    /// (the upstream default), matching the resilient-to-config-typos
    /// philosophy.
    /// </summary>
    public static QuickTerminalPosition Parse(string? raw) => raw switch
    {
        "top" => QuickTerminalPosition.Top,
        "bottom" => QuickTerminalPosition.Bottom,
        "left" => QuickTerminalPosition.Left,
        "right" => QuickTerminalPosition.Right,
        "center" => QuickTerminalPosition.Center,
        _ => QuickTerminalPosition.Top,
    };
}
