namespace Ghostty.Core.Hosting;

/// <summary>
/// The one edge of the quake window that may be resize-dragged: the edge
/// opposite the docked edge. The docked edge (and the two perpendicular edges)
/// sit flush against the monitor and stay fixed. <see cref="None"/> when the
/// window is centered, where no edge is flush to the monitor.
/// </summary>
internal enum QuickTerminalResizeEdge
{
    None,
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// Pure-logic direction helpers for the quake window's non-client frame. The
/// Win32 plumbing (the window-proc subclass and its WM_NCCALCSIZE /
/// WM_NCHITTEST handling) lives in the app's <c>QuickTerminalFrame</c>; this
/// holds the edge math so it can be unit-tested without a real window. All
/// coordinates are physical screen pixels in the same space as the window rect.
/// </summary>
internal static class QuickTerminalFrameGeometry
{
    /// <summary>
    /// The single resizable edge for a docked position: the one opposite the
    /// dock. Center has no flush edge, so it is not edge-resized here.
    /// </summary>
    public static QuickTerminalResizeEdge ResizableEdge(QuickTerminalPosition position) =>
        position switch
        {
            QuickTerminalPosition.Top => QuickTerminalResizeEdge.Bottom,
            QuickTerminalPosition.Bottom => QuickTerminalResizeEdge.Top,
            QuickTerminalPosition.Left => QuickTerminalResizeEdge.Right,
            QuickTerminalPosition.Right => QuickTerminalResizeEdge.Left,
            _ => QuickTerminalResizeEdge.None,
        };

    /// <summary>
    /// The resize edge hit when the point (<paramref name="x"/>,
    /// <paramref name="y"/>) falls within <paramref name="grip"/> pixels of the
    /// resizable edge of the window rect [<paramref name="left"/>,
    /// <paramref name="top"/>, <paramref name="right"/>, <paramref name="bottom"/>);
    /// otherwise <see cref="QuickTerminalResizeEdge.None"/>. This answers
    /// WM_NCHITTEST for the thin non-client strip kept on that edge.
    /// </summary>
    public static QuickTerminalResizeEdge HitTest(
        QuickTerminalPosition position,
        int left, int top, int right, int bottom,
        int x, int y, int grip) =>
        ResizableEdge(position) switch
        {
            QuickTerminalResizeEdge.Bottom => y >= bottom - grip ? QuickTerminalResizeEdge.Bottom : QuickTerminalResizeEdge.None,
            QuickTerminalResizeEdge.Top => y <= top + grip ? QuickTerminalResizeEdge.Top : QuickTerminalResizeEdge.None,
            QuickTerminalResizeEdge.Right => x >= right - grip ? QuickTerminalResizeEdge.Right : QuickTerminalResizeEdge.None,
            QuickTerminalResizeEdge.Left => x <= left + grip ? QuickTerminalResizeEdge.Left : QuickTerminalResizeEdge.None,
            _ => QuickTerminalResizeEdge.None,
        };
}
