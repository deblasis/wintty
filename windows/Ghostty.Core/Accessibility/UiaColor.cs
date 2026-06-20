namespace Ghostty.Core.Accessibility;

/// <summary>
/// Converts a libghostty cell color (<c>0x00RRGGBB</c>) to a Win32 COLORREF
/// (<c>0x00BBGGRR</c>), which is the value UIA expects for the
/// ForegroundColor / BackgroundColor text attributes. Pure.
/// </summary>
internal static class UiaColor
{
    public static int ToColorRef(uint rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return (int)((b << 16) | (g << 8) | r);
    }
}
