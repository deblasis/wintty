using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Ghostty.IconGen;

/// <summary>
/// Renders a single Segoe Fluent Icons glyph to multi-size bitmap
/// masters at the exact frame sizes IcoWriter packs into the final
/// .ico. Used for the auxiliary-window icons (the Settings gear and the
/// inspector bug) so each window is distinguishable from a terminal
/// window in the taskbar group and alt-tab list. The glyph code point
/// matches the in-chrome affordance for that window (SettingsWindow's
/// TitleBar.IconSource, the command palette's inspector entry), so the
/// OS-level slots read the same as the UI.
///
/// Rendering happens at build time on a Windows host (CA1416 is
/// suppressed for IconGen). Segoe Fluent Icons ships with Win11 22H2+;
/// we fall back to Segoe MDL2 Assets (Win10+) when Fluent is absent.
/// Both fonts carry these code points identically at icon sizes.
/// </summary>
internal static class GlyphMasters
{
    // Dual-tone palette: a single tone in either direction goes
    // invisible against the opposite Windows theme (the taskbar
    // tracks dark/light mode and the alt-tab pane tints with the
    // wallpaper). FillColor carries the icon on a dark taskbar;
    // StrokeColor reads on a light-theme one. StrokeColor is not
    // pure black so it does not bloom on ClearType-style subpixel
    // rendering at the 16 px frame.
    private static readonly Color FillColor = Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5);
    private static readonly Color StrokeColor = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);

    public static MasterRasters Render(string glyph)
    {
        var fontName = ResolveIconFontName();
        var dict = new Dictionary<int, Bitmap>();
        // Render one master per IcoWriter frame size so each frame is
        // rasterized directly at its target px; downscaling from a
        // single 256-px master loses fine glyph detail at 16/20/24
        // (exactly where the taskbar lives). Shared array prevents the
        // two size lists from drifting silently.
        foreach (var px in IcoWriter.FrameSizes)
            dict[px] = RenderOne(px, fontName, glyph);
        return MasterRasters.FromDictionary(dict);
    }

    private static Bitmap RenderOne(int px, string fontName, string glyph)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Glyph fills ~72% of the canvas. Slightly tighter than a
        // single-tone render because the stroke adds a halo on top:
        // at the 16 px frame, a 78% glyph + 1 px stroke would clip
        // the outer extents at the canvas edge.
        var fontPx = px * 0.72f;
        using var family = new FontFamily(fontName);
        using var path = new GraphicsPath
        {
            // Glyph paths use non-zero winding; the default Alternate
            // mode would hollow out inner contours, leaving FillPath
            // painting only the thin outline rim.
            FillMode = FillMode.Winding,
        };
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        // GDI+ AddString predates the FontStyle enum: the style
        // parameter is typed as int. Keep the explicit cast --
        // dropping it to FontStyle.Regular fails to compile.
        path.AddString(
            glyph,
            family,
            (int)FontStyle.Regular,
            fontPx,
            new RectangleF(0, 0, px, px),
            fmt);

        // Fill first, stroke on top so the dark outline sits at the
        // glyph edge rather than under the fill.
        using (var brush = new SolidBrush(FillColor))
            g.FillPath(brush, path);

        // The 2.2% ratio crosses 1 px at ~46 px canvas, so for every
        // sub-46 frame (16/20/24/32/40) the floor wins and the stroke
        // is a flat 1 px. That's the design: the floor keeps the
        // outline visible at small sizes; the ratio only kicks in on
        // the 48/64/256 frames so the halo doesn't eat the glyph
        // interior on the 256 frame.
        var strokePx = MathF.Max(px * 0.022f, 1f);
        using var pen = new Pen(StrokeColor, strokePx)
        {
            LineJoin = LineJoin.Round,
        };
        g.DrawPath(pen, path);

        return bmp;
    }

    private static string ResolveIconFontName()
    {
        using var installed = new InstalledFontCollection();
        var families = installed.Families
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (families.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
        if (families.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";

        throw new InvalidOperationException(
            "Cannot render the auxiliary-window .ico: neither 'Segoe Fluent Icons' " +
            "nor 'Segoe MDL2 Assets' is installed on this build host. " +
            "Install one (both ship with Windows 10+ by default) or update " +
            "GlyphMasters to ship its own glyph asset.");
    }
}
