using System;

namespace Ghostty.Core.Hosting;

/// <summary>
/// Resolved quake-window rectangle in physical pixels. Caller
/// hands this straight to <c>AppWindow.MoveAndResize</c>.
/// </summary>
internal readonly record struct QuickTerminalRect(
    int X, int Y, int Width, int Height);

/// <summary>
/// Pure-logic placement resolver for the quake window. Given a
/// position enum, a size spec, and the target monitor's work
/// area, returns the rectangle to position the window at.
/// All inputs and outputs are in the same coordinate space
/// (physical pixels in screen coordinates).
/// </summary>
internal static class QuickTerminalGeometry
{
    // Defaults match upstream: primary axis 50%, secondary 100%.
    private const double DefaultPrimaryPercent = 50.0;

    public static QuickTerminalRect Resolve(
        QuickTerminalPosition position,
        QuickTerminalSize size,
        MonitorBounds monitor)
    {
        // Pick which monitor dimension drives each axis.
        var primaryAxisLength = PrimaryAxisLength(position, monitor);
        var secondaryAxisLength = SecondaryAxisLength(position, monitor);

        var primaryPx = ResolveAxis(size.Primary, primaryAxisLength, isPrimary: true);
        var secondaryPx = ResolveAxis(size.Secondary, secondaryAxisLength, isPrimary: false);

        return position switch
        {
            QuickTerminalPosition.Top    => new(monitor.X, monitor.Y,                              secondaryPx, primaryPx),
            QuickTerminalPosition.Bottom => new(monitor.X, monitor.Bottom - primaryPx,             secondaryPx, primaryPx),
            QuickTerminalPosition.Left   => new(monitor.X, monitor.Y,                              primaryPx,   secondaryPx),
            QuickTerminalPosition.Right  => new(monitor.Right - primaryPx, monitor.Y,              primaryPx,   secondaryPx),
            QuickTerminalPosition.Center => ResolveCenter(monitor, primaryPx, secondaryPx),
            _                            => new(monitor.X, monitor.Y, secondaryPx, primaryPx),
        };
    }

    private static int ResolveAxis(Dimension? d, int parent, bool isPrimary)
    {
        if (d is { } dim)
        {
            return Math.Clamp(dim.ToPixels(parent), 1, parent);
        }
        return isPrimary
            ? Math.Clamp((int)Math.Round(parent * DefaultPrimaryPercent / 100.0), 1, parent)
            : parent;
    }

    private static int PrimaryAxisLength(QuickTerminalPosition position, MonitorBounds monitor) =>
        position switch
        {
            QuickTerminalPosition.Top or QuickTerminalPosition.Bottom => monitor.Height,
            QuickTerminalPosition.Left or QuickTerminalPosition.Right => monitor.Width,
            QuickTerminalPosition.Center => monitor.IsLandscape ? monitor.Height : monitor.Width,
            _ => monitor.Height,
        };

    private static int SecondaryAxisLength(QuickTerminalPosition position, MonitorBounds monitor) =>
        position switch
        {
            QuickTerminalPosition.Top or QuickTerminalPosition.Bottom => monitor.Width,
            QuickTerminalPosition.Left or QuickTerminalPosition.Right => monitor.Height,
            QuickTerminalPosition.Center => monitor.IsLandscape ? monitor.Width : monitor.Height,
            _ => monitor.Width,
        };

    private static QuickTerminalRect ResolveCenter(MonitorBounds monitor, int primaryPx, int secondaryPx)
    {
        // On landscape the primary axis is height; on portrait it's width.
        if (monitor.IsLandscape)
        {
            return new(
                monitor.X + (monitor.Width - secondaryPx) / 2,
                monitor.Y + (monitor.Height - primaryPx) / 2,
                secondaryPx,
                primaryPx);
        }
        return new(
            monitor.X + (monitor.Width - primaryPx) / 2,
            monitor.Y + (monitor.Height - secondaryPx) / 2,
            primaryPx,
            secondaryPx);
    }
}
