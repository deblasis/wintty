using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Ghostty.IconGen;

/// <summary>
/// Renders the Settings gear glyph (Segoe Fluent Icons E713) to
/// multi-size bitmap masters at the exact frame sizes IcoWriter packs
/// into the final .ico. The same code point already drives
/// SettingsWindow's in-chrome TitleBar.IconSource, so the OS-level
/// slots (taskbar group, alt-tab) end up with the same affordance as
/// the title bar.
///
/// Rendering happens at build time on a Windows host (CA1416 is
/// suppressed for IconGen). Segoe Fluent Icons ships with Win11 22H2+;
/// we fall back to Segoe MDL2 Assets (Win10+) when Fluent is absent.
/// Both fonts carry E713 at the same code point and render visually
/// identically at icon sizes.
/// </summary>
internal static class GearMasters
{
    // U+E713 = "Settings" in both Segoe Fluent Icons and the older
    // Segoe MDL2 Assets. Matches SettingsWindow.xaml's FontIconSource.
    private const string GearGlyph = "\uE713";

    // Dual-tone palette: a single tone in either direction goes
    // invisible against the opposite Windows theme (the taskbar
    // tracks dark/light mode and the alt-tab pane tints with the
    // wallpaper). FillColor carries the icon on a dark taskbar;
    // StrokeColor reads on a light-theme one. StrokeColor is not
    // pure black so it does not bloom on ClearType-style subpixel
    // rendering at the 16 px frame.
    private static readonly Color FillColor = Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5);
    private static readonly Color StrokeColor = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);

    public static MasterRasters Render()
    {
        var fontName = ResolveIconFontName();
        var dict = new Dictionary<int, Bitmap>();
        // Render one master per IcoWriter frame size so each frame is
        // rasterized directly at its target px; downscaling from a
        // single 256-px master loses the gear-tooth detail at 16/20/24
        // (exactly where the taskbar lives). Shared array prevents the
        // two size lists from drifting silently.
        foreach (var px in IcoWriter.FrameSizes)
            dict[px] = RenderOne(px, fontName);
        return MasterRasters.FromDictionary(dict);
    }

    private static Bitmap RenderOne(int px, string fontName)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Glyph fills ~72% of the canvas. Slightly tighter than a
        // single-tone render because the stroke adds a halo on top:
        // at the 16 px frame, a 78% glyph + 1 px stroke would clip
        // the outer teeth at the canvas edge.
        var fontPx = px * 0.72f;
        using var family = new FontFamily(fontName);
        using var path = new GraphicsPath();
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        path.AddString(
            GearGlyph,
            family,
            (int)FontStyle.Regular,
            fontPx,
            new RectangleF(0, 0, px, px),
            fmt);
        // Glyph paths use non-zero winding; the default Alternate
        // mode would hollow out the inner contours (the gear arms and
        // hub), leaving FillPath painting only the thin outline rim.
        path.FillMode = FillMode.Winding;

        // Fill first, stroke on top so the dark outline sits at the
        // glyph edge rather than under the fill.
        using (var brush = new SolidBrush(FillColor))
            g.FillPath(brush, path);

        // Stroke scales with canvas, clamped >= 1 px so the small
        // frames don't antialias the outline into nothing. The 2.2%
        // ratio gives a halo that's perceptible on a light taskbar
        // without eating the arm interior on the 256 frame.
        var strokePx = MathF.Max(px * 0.022f, 1f);
        using var pen = new Pen(StrokeColor, strokePx)
        {
            LineJoin = LineJoin.Round,
            MiterLimit = 1f,
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
            "Cannot render the Settings gear .ico: neither 'Segoe Fluent Icons' " +
            "nor 'Segoe MDL2 Assets' is installed on this build host. " +
            "Install one (both ship with Windows 10+ by default) or update " +
            "GearMasters to ship its own glyph asset.");
    }
}
