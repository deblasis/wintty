namespace Ghostty.Core.Session;

/// <summary>
/// Decides whether a saved window geometry is worth honouring, and
/// normalizes it into a concrete rect. Pure, so the rules are testable
/// without a window.
///
/// <para>Shared because two callers must agree on them and cannot see each
/// other: the window places itself from this, and the pre-XAML splash sizes
/// itself to cover the window it is about to hide. A rect one of them
/// accepts and the other rejects puts the splash somewhere the window is
/// not, which is the whole failure the splash exists to prevent. They were
/// previously two copies of the same literals kept in step by a comment.</para>
///
/// <para>Deliberately stops short of asking where the monitors are. The
/// window answers that with WinUI's DisplayArea and the splash with
/// MonitorFromRect, because the splash runs on its own thread before any
/// WinUI type exists -- so the on-screen test stays with each caller and
/// only the size and position rules live here.</para>
/// </summary>
internal static class WindowGeometryGate
{
    /// <summary>
    /// Smallest saved size worth believing. Closing while minimized saves a
    /// 160x31 rect at (-32000,-32000); honouring that would put the window
    /// and the splash off-screen.
    /// </summary>
    public const int MinWidth = 200;
    public const int MinHeight = 150;

    /// <summary>
    /// Turn a saved geometry into a rect, or fail when it is too small to
    /// be real. A null position normalizes to the origin rather than
    /// failing: it means "wherever the OS put it", which is a reason to
    /// pick a corner, not a reason to discard a perfectly good size.
    /// </summary>
    public static bool TryNormalize(
        WindowGeometry? geometry, out (int X, int Y, int Width, int Height) rect)
    {
        rect = default;
        if (geometry is null) return false;
        if (geometry.Width is not int w || w < MinWidth) return false;
        if (geometry.Height is not int h || h < MinHeight) return false;

        rect = (geometry.X ?? 0, geometry.Y ?? 0, w, h);
        return true;
    }
}
