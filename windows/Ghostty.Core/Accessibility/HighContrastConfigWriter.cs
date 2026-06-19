using System.Globalization;
using System.Text;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Renders the config override body layered on top of the user's config
/// when Windows High Contrast is active. Pure string formatting so it can
/// be unit-tested without the XAML runtime or libghostty.
/// </summary>
public static class HighContrastConfigWriter
{
    /// <summary>
    /// WCAG AAA contrast ratio. Bumps libghostty's per-cell minimum-contrast
    /// so ANSI-colored output stays legible against the HC background without
    /// any renderer change.
    /// </summary>
    public const int DefaultMinimumContrast = 7;

    /// <summary>
    /// Convert a Win32 COLORREF (0x00BBGGRR) to Ghostty's lowercase
    /// zero-padded #rrggbb config form.
    /// </summary>
    public static string FormatColor(uint colorRef)
    {
        var r = colorRef & 0xFF;
        var g = (colorRef >> 8) & 0xFF;
        var b = (colorRef >> 16) & 0xFF;
        return string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}");
    }

    public static string Render(
        HighContrastColors colors,
        int minimumContrast = DefaultMinimumContrast)
    {
        // '\n' line endings: libghostty's line parser is newline-agnostic
        // and the file is written/consumed by us, never edited by hand.
        var sb = new StringBuilder();
        sb.Append("background = ").Append(FormatColor(colors.Background)).Append('\n');
        sb.Append("foreground = ").Append(FormatColor(colors.Foreground)).Append('\n');
        sb.Append("selection-background = ").Append(FormatColor(colors.SelectionBackground)).Append('\n');
        sb.Append("selection-foreground = ").Append(FormatColor(colors.SelectionForeground)).Append('\n');
        sb.Append("minimum-contrast = ")
          .Append(minimumContrast.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }
}
