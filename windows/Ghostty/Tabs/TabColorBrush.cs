using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Bridge between the palette in Ghostty.Core, which speaks
/// <see cref="System.Drawing.Color"/> and packed sRGB because it has no WinUI
/// dependency, and the brushes the strips and previews paint with.
/// </summary>
internal static class TabColorBrush
{
    public static SolidColorBrush From(System.Drawing.Color drawing)
        => new(Windows.UI.Color.FromArgb(
            drawing.A, drawing.R, drawing.G, drawing.B));

    /// <summary>
    /// Brush from a 0x00RRGGBB value, as returned by the palette's
    /// foreground and effective-background helpers. Always opaque: the
    /// packed form carries no alpha.
    /// </summary>
    public static SolidColorBrush FromPackedRgb(uint packed)
        => new(Windows.UI.Color.FromArgb(
            0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed));
}
