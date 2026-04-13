using Ghostty.Core.Tabs;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Ghostty.Tabs;

/// <summary>
/// Resolves the target display for a source window and applies a
/// Snap Layouts zone to a target window via
/// <see cref="AppWindow.MoveAndResize(RectInt32, DisplayArea)"/>.
///
/// Uses <see cref="DisplayArea.GetFromWindowId"/> with
/// <see cref="DisplayAreaFallback.Nearest"/> so a monitor disconnect
/// between menu open and menu click degrades gracefully to the
/// nearest available display. Never calls
/// <see cref="DisplayArea.FindAll"/> (known iteration bug in the
/// microsoft-ui-xaml repo).
/// </summary>
internal static class SnapPlacement
{
    public static DisplayArea ResolveDisplayFor(AppWindow source) =>
        DisplayArea.GetFromWindowId(source.Id, DisplayAreaFallback.Nearest);

    public static void ApplyZone(AppWindow target, DisplayArea display, SnapZone zone)
    {
        var work = display.WorkArea; // RectInt32
        var rect = SnapZoneMath.RectFor(zone, work.X, work.Y, work.Width, work.Height);
        // Two-argument MoveAndResize takes absolute screen coordinates.
        // SnapZoneMath.RectFor already produces absolute coords because
        // we pass the work area's origin (work.X, work.Y) above.
        target.MoveAndResize(rect.ToRectInt32(), display);
    }
}
