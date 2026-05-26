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

    // Off-white reads on both the default dark taskbar and the
    // translucent alt-tab pane.
    private static readonly Color GearColor = Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6);

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
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Glyph fills ~78% of the canvas so the gear teeth retain a
        // pixel of padding from the edge at the 16 px frame (otherwise
        // they clip when GDI+ rounds the layout box).
        var fontPx = px * 0.78f;
        using var font = new Font(fontName, fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(GearColor);
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        g.DrawString(GearGlyph, font, brush, new RectangleF(0, 0, px, px), fmt);
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
