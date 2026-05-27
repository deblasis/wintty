namespace Ghostty.Core.Hosting;

/// <summary>
/// Which monitor the quake terminal targets. Upstream also exposes
/// `macos-menu-bar`; that's mac-only and not honored here.
/// </summary>
internal enum QuickTerminalScreen
{
    Main,
    Mouse,
}

internal static class QuickTerminalScreenExtensions
{
    public static QuickTerminalScreen Parse(string? raw) => raw switch
    {
        "main" => QuickTerminalScreen.Main,
        "mouse" => QuickTerminalScreen.Mouse,
        "macos-menu-bar" => QuickTerminalScreen.Main, // mac-only, treat as Main
        _ => QuickTerminalScreen.Main,
    };
}
