using System;
using System.Collections.Generic;
using Ghostty.Core.Config;
using Ghostty.Core.Windows;

namespace Ghostty.Core.Themes;

/// <summary>
/// The colours a theme row in the command palette draws its swatch from: the
/// terminal background and foreground, the cursor, and the sixteen ANSI
/// palette entries, as the theme file states them.
///
/// Read straight from the file rather than through libghostty: the palette
/// shows many rows, and building a native config per row to ask it would be
/// the wrong cost for a thumbnail. What the file leaves unset falls back to
/// libghostty's own defaults, so a sparse theme is drawn the way the terminal
/// would show it rather than on black.
/// </summary>
public sealed record ThemeSwatch(
    uint Background,
    uint Foreground,
    uint Cursor,
    IReadOnlyList<uint> Palette)
{
    /// <summary>libghostty's default background (src/config/Config.zig).</summary>
    public const uint DefaultBackground = 0x282C34;

    /// <summary>libghostty's default foreground (src/config/Config.zig).</summary>
    public const uint DefaultForeground = 0xFFFFFF;

    /// <summary>
    /// libghostty's default first sixteen palette entries, from Name.default
    /// in src/terminal/color.zig; the same table ConfigService falls back to.
    /// </summary>
    public static IReadOnlyList<uint> DefaultPalette { get; } =
    [
        0x1D1F21, 0xCC6666, 0xB5BD68, 0xF0C674,
        0x81A2BE, 0xB294BB, 0x8ABEB7, 0xC5C8C6,
        0x666666, 0xD54E53, 0xB9CA4A, 0xE7C547,
        0x7AA6DA, 0xC397D8, 0x70C0B1, 0xEAEAEA,
    ];

    /// <summary>
    /// Whether the theme is a dark one, by the same luminance test the window
    /// chrome uses to pick its own light or dark variant from a background.
    /// </summary>
    public bool IsDark => ThemeResolution.IsBackgroundDark(Background);

    /// <summary>
    /// The swatch a theme file's lines describe. Later lines override earlier
    /// ones, as in any config file; comments, blank lines and values that are
    /// not a colour are skipped. An unset cursor colour is the foreground,
    /// which is what the chrome resolves it to (ConfigService.ApplyThemeColors).
    /// </summary>
    public static ThemeSwatch Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var background = DefaultBackground;
        var foreground = DefaultForeground;
        uint? cursor = null;
        var palette = new uint[16];
        for (var i = 0; i < 16; i++) palette[i] = DefaultPalette[i];
        var paletteValues = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = Unquote(trimmed[(eq + 1)..].Trim());

            switch (key)
            {
                case "background":
                    if (ThemeParser.TryParseHexRgb(value, out var bg)) background = bg;
                    break;
                case "foreground":
                    if (ThemeParser.TryParseHexRgb(value, out var fg)) foreground = fg;
                    break;
                case "cursor-color":
                    if (ThemeParser.TryParseHexRgb(value, out var cc)) cursor = cc;
                    break;
                case "palette":
                    paletteValues.Add(value);
                    break;
            }
        }

        ThemeParser.ApplyPaletteFromValues(paletteValues, palette);
        return new ThemeSwatch(background, foreground, cursor ?? foreground, palette);
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
