using System;

namespace Ghostty.Core.Windows;

/// <summary>
/// DPI-scaled, work-area-clamped placement for a window that declares its
/// size in design (100%-scaling) pixels. Pure: no Win32, no WinAppSDK, so
/// the numbers are unit-testable without a window. The rect shapes it
/// speaks (<see cref="WorkAreaRect"/>, <see cref="PlacementRect"/>) are
/// declared beside <see cref="CursorWindowPlacement"/>, which needs the same
/// pair for the same reason.
///
/// Why it exists: <c>AppWindow.MoveAndResize</c> takes PHYSICAL pixels.
/// A window that hands it its design constants opens at 733x500 on a 150%
/// monitor, and a window taller than the work area centers to a negative
/// offset and starts above the top of the screen. Both are arithmetic, so
/// both are pinned here rather than re-derived in each constructor.
///
/// Contract, in the order the calls happen:
///
///   - The design size is scaled by the window's own DPI over
///     <see cref="DefaultDpi"/>. A DPI of 0 -- what a failed monitor DPI
///     read yields (no monitor, or one the process cannot read) --
///     is taken as unscaled, which is what the shader gallery picker
///     already settled on.
///   - The scaled size is clamped to the work area on each axis. Bigger than
///     the work area is not a licence to hang off the screen: the window
///     gets the work area and the OS clips whatever the content needs.
///   - The window is centered on what is left, then the origin is clamped.
///     With the size already clamped this is belt and braces, and it is
///     here because the caller is one arithmetic slip away from a negative
///     X on a monitor left of the primary.
/// </summary>
internal static class DpiScaledWindowPlacement
{
    /// <summary>The DPI a design size is authored against (100% scaling).</summary>
    public const uint DefaultDpi = 96;

    /// <summary>
    /// The scale factor a design size is multiplied by at
    /// <paramref name="dpi"/>. Zero is the unreadable-DPI case, not a
    /// zero-sized window.
    /// </summary>
    public static double Scale(uint dpi) =>
        dpi == 0 ? 1.0 : dpi / (double)DefaultDpi;

    /// <summary>
    /// The rect a window of this design size takes at this DPI, on this
    /// display's work area.
    /// </summary>
    public static PlacementRect Compute(
        int designWidth,
        int designHeight,
        uint dpi,
        WorkAreaRect workArea)
    {
        var scale = Scale(dpi);
        var width = Fit((int)Math.Round(designWidth * scale), workArea.Width);
        var height = Fit((int)Math.Round(designHeight * scale), workArea.Height);

        // Math.Clamp throws when the bounds cross, so the upper bound is
        // taken as at least the lower one; with the size already fitted that
        // only matters for a degenerate work area.
        var x = Math.Clamp(
            workArea.X + (workArea.Width - width) / 2,
            workArea.X,
            Math.Max(workArea.X, workArea.Right - width));
        var y = Math.Clamp(
            workArea.Y + (workArea.Height - height) / 2,
            workArea.Y,
            Math.Max(workArea.Y, workArea.Bottom - height));

        return new PlacementRect(x, y, width, height);
    }

    /// <summary>
    /// One axis of the scaled size, held inside what the work area offers.
    /// Never zero, whatever the work area says: a zero-sized window is one
    /// the OS answers with its own minimum, somewhere we did not choose.
    /// </summary>
    private static int Fit(int scaled, int available) =>
        Math.Min(Math.Max(scaled, 1), Math.Max(available, 1));
}
