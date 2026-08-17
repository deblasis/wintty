namespace Ghostty.Core.Tabs;

/// <summary>
/// TitleChanged from a surface that is no longer the active leaf must
/// not write into <see cref="TabManager.ActiveTab"/>. Dispose of a
/// closing tab queues those callbacks after ActiveTab has already
/// switched, which used to stamp the remaining tab with the dead
/// tab's last OSC title (e.g. cmd.exe).
/// </summary>
public static class LiveTitleGuard
{
    public static bool Accepts(object? sender, object? activeTerminal)
        => activeTerminal is not null && ReferenceEquals(sender, activeTerminal);
}
