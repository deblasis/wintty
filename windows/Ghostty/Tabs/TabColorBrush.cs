using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Bridge between the palette in Ghostty.Core, which speaks
/// <see cref="System.Drawing.Color"/> and packed sRGB because it has no WinUI
/// dependency, and the brushes the strips and previews paint with.
/// </summary>
/// <remarks>
/// One instance per packed colour, shared by every painter.
///
/// Sharing is load-bearing, not an allocation nicety. The strips hand these
/// brushes to element properties AND to per-item resource-dictionary entries
/// that MUXC's own visual-state setters resolve <c>{ThemeResource}</c>
/// references through; a fresh instance per pass orphaned the pass-before's
/// resolved value on every selection sync, and a layout switch interleaved
/// with selections then re-resolved such a reference onto a freed CDO,
/// faulting inside MUXC's own setter walk (<c>CDependencyObject::SetParent</c>).
/// A shared instance makes a rewrite of the same ink a
/// stable assignment and a removal a no-op for the object's lifetime:
/// nothing a chrome pass wrote is ever freed under a live reference.
///
/// Callers must not mutate the returned brush (none does: the only brush
/// whose colour is written in the shell is ActiveFieldFill's own instance).
/// </remarks>
internal static class TabColorBrush
{
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> Cache = new();

    public static SolidColorBrush From(System.Drawing.Color drawing)
        => FromPackedArgb(unchecked((uint)drawing.ToArgb()));

    /// <summary>
    /// Brush from a 0x00RRGGBB value, as returned by the palette's
    /// foreground and effective-background helpers. Always opaque: the
    /// packed form carries no alpha.
    /// </summary>
    public static SolidColorBrush FromPackedRgb(uint packed)
        => FromPackedArgb(0xFF000000u | packed);

    /// <summary>
    /// Brush from a 0xAARRGGBB value, for the one thing in the strips that
    /// is deliberately not opaque: the group field's ink wash, which has to
    /// let Mica through or it stops being a wash.
    /// </summary>
    public static SolidColorBrush FromPackedArgb(uint packed)
        => Cache.GetOrAdd(packed, static p => new SolidColorBrush(
            Windows.UI.Color.FromArgb(
                (byte)(p >> 24), (byte)(p >> 16), (byte)(p >> 8), (byte)p)));
}
