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
    public static int WordAlignedStride(int width) => ((width + 15) / 16) * 2;
}
