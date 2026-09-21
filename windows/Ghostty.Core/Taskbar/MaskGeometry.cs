using System;

namespace Ghostty.Core.Taskbar;

/// <summary>
/// Scanline geometry for the 1-bpp AND mask that pairs with a taskbar
/// overlay icon's 32-bpp color plane.
///
/// It lives here, away from the GDI call that consumes it, because it is
/// the only part of building that mask which is plain arithmetic. Get it
/// wrong and CreateBitmap is handed a buffer shorter than the bitmap it
/// describes, and GDI reads off the end of a pinned array on every badge.
/// The shell assembly cannot be loaded into a test host, so arithmetic
/// left over there can only ever be guarded by reading the source, which
/// is a weaker thing than running it.
/// </summary>
internal static class MaskGeometry
{
    /// <summary>
    /// Bytes per scanline for a <paramref name="width"/>-pixel 1-bpp
    /// bitmap.
    ///
    /// GDI aligns 1-bpp scanlines on a WORD boundary, so this is the byte
    /// count the pixels need, rounded up to a whole number of 16-bit
    /// words: never shorter than ceil(width / 8), always even, and never
    /// more than one byte past the minimum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> is zero or negative.
    /// </exception>
    public static int WordAlignedStride(int width)
    {
        // A non-positive width has no stride, and returning 0 would be the
        // quiet form of the very defect this type was extracted to close:
        // a zero-length buffer pins to a null pointer, which is precisely
        // what handing CreateBitmap a null lpvBits does, undefined mask and
        // all. Callers clamp their metrics, so this is unreachable. It
        // throws rather than returning 0 so that losing a clamp fails
        // loudly instead of silently reintroducing undefined bits.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        return ((width + 15) / 16) * 2;
    }
}
