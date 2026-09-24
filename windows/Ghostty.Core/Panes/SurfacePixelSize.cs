using System;

namespace Ghostty.Core.Panes;

/// <summary>
/// A terminal panel's size in device pixels, as libghostty is told it. One
/// formula for creation and for every resize, so the size a surface is
/// created at and the first size pushed after it agree to the pixel.
/// </summary>
internal static class SurfacePixelSize
{
    /// <summary>
    /// The largest texture edge the renderers can allocate. D3D12 at feature
    /// level 12_0 and D3D11 at 11_0 both cap a 2D texture at 16384, and the
    /// swap chain is created at the surface's initial size.
    /// </summary>
    internal const uint MaxTextureEdge = 16384;

    /// <summary>
    /// Device pixels for a panel of <paramref name="widthDips"/> by
    /// <paramref name="heightDips"/> at the panel's composition scale. A
    /// scale that is not positive counts as 1, the product is truncated, and
    /// each edge is at least 1. This is what every resize pushes.
    /// </summary>
    internal static (uint Width, uint Height) FromDips(
        double widthDips, double heightDips, double scaleX, double scaleY)
    {
        var sx = scaleX > 0 ? scaleX : 1.0;
        var sy = scaleY > 0 ? scaleY : 1.0;
        return ((uint)Math.Max(1, widthDips * sx), (uint)Math.Max(1, heightDips * sy));
    }

    /// <summary>
    /// The size to create a surface at, or null while the panel has not been
    /// measured (an edge that is zero, negative or NaN: a hidden tab, or a
    /// control that has not been laid out). Otherwise <see cref="FromDips"/>,
    /// with each edge capped at <see cref="MaxTextureEdge"/> so creation never
    /// asks the renderer for a swap chain it cannot allocate. A larger panel
    /// is still pushed at its real size afterwards, where the renderer logs
    /// the failed resize and the app keeps running, as it always has.
    /// </summary>
    internal static (uint Width, uint Height)? Initial(
        double widthDips, double heightDips, double scaleX, double scaleY)
    {
        if (!(widthDips > 0) || !(heightDips > 0)) return null;
        var sx = scaleX > 0 ? scaleX : 1.0;
        var sy = scaleY > 0 ? scaleY : 1.0;
        // Clamp before the cast: a double past uint's range does not
        // convert to anything meaningful.
        var w = Math.Min(widthDips * sx, MaxTextureEdge);
        var h = Math.Min(heightDips * sy, MaxTextureEdge);
        return FromDips(w, h, 1.0, 1.0);
    }
}
