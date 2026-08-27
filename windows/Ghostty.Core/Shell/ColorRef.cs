namespace Ghostty.Core.Shell;

/// <summary>
/// Conversion between the two ways a colour is spelled on this window.
/// XAML and <see cref="RootBackgroundResolver"/> speak ARGB; GDI speaks
/// COLORREF, which orders the channels the other way round and has no
/// alpha at all.
/// </summary>
public static class ColorRef
{
    /// <summary>
    /// ARGB (0xAARRGGBB) to COLORREF (0x00BBGGRR).
    ///
    /// The two spellings agree on greys, which is why the class brush went
    /// so long handing CreateSolidBrush a hardcoded #0C0C0C with the
    /// channels never converted and nothing looking wrong.
    /// </summary>
    public static uint ToColorRef(uint argb) =>
        ((argb & 0x00FF0000u) >> 16) | (argb & 0x0000FF00u) | ((argb & 0x000000FFu) << 16);
}
