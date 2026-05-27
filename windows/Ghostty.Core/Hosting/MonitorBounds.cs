namespace Ghostty.Core.Hosting;

/// <summary>
/// Pure-data input to <see cref="QuickTerminalGeometry"/>.
/// The caller (Win32 adapter) fills this from `GetMonitorInfo`'s
/// `rcWork`; values are physical pixels.
/// </summary>
internal readonly record struct MonitorBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>
    /// True when the monitor is wider than (or equal to) it is tall.
    /// Square monitors (Width == Height) intentionally land here so
    /// the Center-position primary-axis pick is deterministic in
    /// the tie case.
    /// </summary>
    public bool IsLandscape => Width >= Height;
}
