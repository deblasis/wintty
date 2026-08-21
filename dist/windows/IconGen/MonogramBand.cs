using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Ghostty.IconGen;

/// <summary>
/// Draws the edition's coloured band, and its letters where they fit.
///
/// Geometry and silhouette clipping belong to <see cref="BottomBand"/>;
/// this fills the rectangle it is handed and puts type in it.
///
/// Below <see cref="EditionBrand.MinLetterSizePx"/> the letters are dropped
/// and the band is drawn as a plain coloured bar. That is the honest floor:
/// a three-glyph monogram rendered into four pixels is not a small monogram,
/// it is a smudge that reads as a rendering fault. At those sizes the colour
/// is the whole signal, and the flagship having no band at all is the other
/// half of it.
/// </summary>
internal static class MonogramBand
{
    public static void Apply(Bitmap bitmap, EditionBrand brand, Rectangle band)
    {
        if (string.IsNullOrEmpty(brand.Monogram)) return;

        int size = bitmap.Width;

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        // Grid-fit, never ClearType: subpixel anti-aliasing on a transparent
        // surface leaves colour fringes in the exported PNG.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var brush = new SolidBrush(brand.BandFill))
            g.FillRectangle(brush, band);

        // A hairline along the top edge so the band reads as a deliberate
        // layer rather than the screen changing colour halfway down. Two
        // pixels per 256, not four: at four it was a tenth of the band on
        // the largest master.
        int rule = Math.Max(1, (int)Math.Round(size / 256.0 * 2));
        using (var shade = new SolidBrush(Color.FromArgb(0x59, 0, 0, 0)))
            g.FillRectangle(shade, band.X, band.Y, band.Width, rule);

        if (size >= EditionBrand.MinLetterSizePx)
            DrawMonogram(g, brand, size, new Rectangle(
                band.X, band.Y + rule, band.Width, band.Height - rule));
    }

    private static void DrawMonogram(Graphics g, EditionBrand brand, int size, Rectangle inner)
    {
        using var family = ResolveFamily();
        using var brush = new SolidBrush(brand.BandInk);

        // Sized from the INK, measured, rather than from font metrics.
        //
        // The obvious approach - em = some fraction of the band, shrink while
        // Font.GetHeight exceeds it - sets the type far too small, because
        // GetHeight is the line box: ascent, descent and leading, about 1.33
        // em, for a monogram whose caps occupy nearer 0.7. Constraining that
        // against a 9 px band left "PRO" at a 6 px em, which renders as a
        // dozen anti-aliased pixels rather than letters.
        //
        // So: render once at a known em, measure the ink it actually put
        // down, and scale. Two passes of a tool that runs at build time, and
        // it is exact for whatever font resolved rather than for the one
        // whose ratios were hard-coded.
        const float probeEm = 64f;
        var probe = MeasureInk(family, brand.Monogram, probeEm);
        if (probe.IsEmpty) return;

        // The plate's corner arcs eat roughly 8 percent of the width per
        // side at the band's vertical middle, so hold the letters inside 70
        // percent. 0.78 of the band height leaves the caps a little air top
        // and bottom without touching the rule.
        float byHeight = inner.Height * 0.78f / probe.Height * probeEm;
        float byWidth = size * 0.70f / probe.Width * probeEm;
        float em = Math.Max(4f, Math.Min(byHeight, byWidth));

        using var font = new Font(family, em, FontStyle.Bold, GraphicsUnit.Pixel);
        float scale = em / probeEm;
        float inkWidth = probe.Width * scale;
        float inkHeight = probe.Height * scale;

        // probe.X/Y are where the ink began relative to the draw origin, so
        // subtracting them puts the ink itself where we want it rather than
        // the glyph box that contains it.
        float x = inner.X + (inner.Width - inkWidth) / 2f - probe.X * scale;
        float y = inner.Y + (inner.Height - inkHeight) / 2f - probe.Y * scale;

        // Drawn glyph by glyph because GDI+ DrawString has no tracking, and
        // caps this small read as a block without a little air between them.
        foreach (char ch in brand.Monogram)
        {
            var one = ch.ToString();
            g.DrawString(one, font, brush, x, y, StringFormat.GenericTypographic);
            x += g.MeasureString(one, font, PointF.Empty, StringFormat.GenericTypographic).Width
                 + em * 0.02f;
        }
    }

    /// <summary>
    /// Where the monogram's ink lands when drawn at <paramref name="em"/>,
    /// relative to the draw origin. Rendered white on black and scanned,
    /// which measures the glyphs rather than the box the font reserves.
    /// </summary>
    private static RectangleF MeasureInk(FontFamily family, string text, float em)
    {
        int canvas = (int)Math.Ceiling(em * 4);
        using var probe = new Bitmap(canvas, canvas, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(probe))
        using (var font = new Font(family, em, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float x = 0;
            foreach (char ch in text)
            {
                var one = ch.ToString();
                g.DrawString(one, font, Brushes.White, x, 0, StringFormat.GenericTypographic);
                x += g.MeasureString(one, font, PointF.Empty, StringFormat.GenericTypographic).Width
                     + em * 0.02f;
            }
        }

        int minX = canvas, minY = canvas, maxX = -1, maxY = -1;
        for (int py = 0; py < canvas; py++)
            for (int px = 0; px < canvas; px++)
                if (probe.GetPixel(px, py).R > 96)
                {
                    if (px < minX) minX = px;
                    if (py < minY) minY = py;
                    if (px > maxX) maxX = px;
                    if (py > maxY) maxY = py;
                }

        if (maxX < 0) return RectangleF.Empty;
        return new RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static float MeasureAdvance(Graphics g, string text, Font font, float em)
    {
        float total = 0f;
        foreach (char ch in text)
        {
            total += g.MeasureString(
                ch.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width;
            total += em * 0.02f;
        }
        return total - em * 0.02f;
    }

    /// <summary>
    /// Segoe UI where it exists, which is everywhere this tool runs, and
    /// the generic sans otherwise. The fallback resolves to Microsoft Sans
    /// Serif, whose small caps are visibly worse, so it is a fallback and
    /// not a choice.
    /// </summary>
    private static FontFamily ResolveFamily()
    {
        try
        {
            return new FontFamily("Segoe UI");
        }
        catch (ArgumentException)
        {
            return new FontFamily(GenericFontFamilies.SansSerif);
        }
    }
}
