namespace Ghostty.Core.Windows;

/// <summary>
/// Work area rectangle in physical screen pixels. Mirrors the shape of
/// <c>DisplayArea.WorkArea</c> without pulling in WinAppSDK types, so
/// the placement math can live in <c>Ghostty.Core</c> and be unit-tested
/// without a WinUI runtime.
/// </summary>
internal readonly record struct WorkAreaRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Target placement rectangle for a new window. Mirrors
/// <c>Windows.Graphics.RectInt32</c>.
/// </summary>
internal readonly record struct PlacementRect(int X, int Y, int Width, int Height);

/// <summary>
/// Cursor-anchored placement math for Move Tab to New Window. Pure.
/// No Win32, no WinAppSDK. Called from MainWindow.DetachTabToNewWindow
/// after <c>GetCursorPos</c> and <c>DisplayArea.WorkArea</c> have been
/// resolved.
///
/// Contract:
///
///   - Raw anchor is (cursor - (<see cref="OffsetX"/>, <see cref="OffsetY"/>)).
///     The offset keeps the title bar of the new window under the cursor
///     without putting the cursor directly on top of a window control.
///   - If the resulting rect extends past the work area's right or
///     bottom edge, the top-left is pulled back so the entire window
///     fits.
///   - If the window is larger than the work area on either axis, the
///     top-left is pinned to the work area origin on that axis and the
///     size is left unchanged. The system will clip; we do not shrink.
/// </summary>
internal static class CursorWindowPlacement
{
    // TODO(dpi): scale offset constants per monitor DPI. At 100% DPI a
    // 32px nudge puts the title bar under the cursor; at 200% it feels
    // half as large. Left as a fixed physical-pixel offset for the
    // first cut; revisit when the new window's target DisplayArea
    // reports effective DPI.
    internal const int OffsetX = 32;
    internal const int OffsetY = 32;

    public static PlacementRect Compute(
        int cursorX,
        int cursorY,
        int windowWidth,
        int windowHeight,
        WorkAreaRect workArea)
    {
        int x = cursorX - OffsetX;
        int y = cursorY - OffsetY;

        // Window wider than the work area: pin left, do not shrink.
        if (windowWidth >= workArea.Width)
        {
            x = workArea.X;
        }
        else
        {
            // Clamp right edge into work area, then clamp left edge.
            if (x + windowWidth > workArea.Right) x = workArea.Right - windowWidth;
            if (x < workArea.X) x = workArea.X;
        }

        if (windowHeight >= workArea.Height)
        {
            y = workArea.Y;
        }
        else
        {
            if (y + windowHeight > workArea.Bottom) y = workArea.Bottom - windowHeight;
            if (y < workArea.Y) y = workArea.Y;
        }

        return new PlacementRect(x, y, windowWidth, windowHeight);
    }
}
