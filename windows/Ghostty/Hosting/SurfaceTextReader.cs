using Ghostty.Interop;

namespace Ghostty.Hosting;

/// <summary>
/// Reads a surface's whole visible viewport as plain text, for the tab-overview
/// preview. The caller's <c>PreviewTextFormatter</c> then trims trailing blank
/// lines and keeps the LAST few - so the preview shows the most recent content
/// (the prompt / last commands) wherever it sits in the viewport, including the
/// common case of a short session whose prompt is near the top with blank rows
/// below. Returns null for a dead/zero surface so the caller can show a
/// placeholder. The only FFI seam for previews.
/// </summary>
internal static class SurfaceTextReader
{
    public static string? Read(System.IntPtr surfaceHandle)
    {
        if (surfaceHandle == System.IntPtr.Zero) return null;
        var surface = new GhosttySurface(surfaceHandle);

        var selection = new GhosttySelection
        {
            TopLeft = new GhosttyPoint
            {
                Tag = GhosttyPointTag.Viewport,
                Coord = GhosttyPointCoord.TopLeft,
                X = 0,
                Y = 0,
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
