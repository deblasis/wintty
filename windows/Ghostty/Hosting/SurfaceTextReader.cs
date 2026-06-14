using Ghostty.Interop;

namespace Ghostty.Hosting;

/// <summary>
/// Reads the bottom <c>rows</c> of a surface's viewport as plain text, for the
/// tab-overview preview. Anchored at the viewport bottom (near the prompt /
/// most recent output), not the top. Returns null for a dead/zero surface so the
/// caller can show a placeholder. The only FFI seam for previews.
/// </summary>
internal static class SurfaceTextReader
{
    public static string? Read(System.IntPtr surfaceHandle, int rows)
    {
        if (surfaceHandle == System.IntPtr.Zero || rows <= 0) return null;
        var surface = new GhosttySurface(surfaceHandle);

        var size = NativeMethods.SurfaceSize(surface);
        if (size.Rows == 0) return null;
        var startRow = size.Rows > rows ? (uint)(size.Rows - rows) : 0u;

        var selection = new GhosttySelection
        {
            TopLeft = new GhosttyPoint
            {
                Tag = GhosttyPointTag.Viewport,
                Coord = GhosttyPointCoord.Exact,
                X = 0,
                Y = startRow,
            },
            BottomRight = new GhosttyPoint
            {
                Tag = GhosttyPointTag.Viewport,
                Coord = GhosttyPointCoord.BottomRight,
                X = 0,
                Y = 0,
            },
            Rectangle = 0,
        };

        return NativeMethods.SurfaceReadText(surface, selection);
    }
}
