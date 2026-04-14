using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ghostty.Core.Config;

/// <summary>
/// Pure-logic parsing for ghostty theme configuration values.
/// Mirrors the Zig parser in Config.zig (Theme.parseCLI) for the
/// conditional theme syntax, and parses palette entries from theme
/// files for the C# side that needs them for UI accents.
/// </summary>
public static class ThemeParser
{
    /// <summary>
    /// Parse a theme config value into light/dark components.
    /// Handles single ("ThemeName") and pair ("light:X,dark:Y") forms.
    /// Returns (null, null) for single themes or empty input. Both
    /// light and dark must be present for a pair; partial pairs (only
    /// light: or only dark:) return (null, null) to match the Zig
    /// parser which treats those as InvalidValue errors.
    /// </summary>
    public static (string? light, string? dark) ParseThemePair(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
            return (null, null);

        var parts = theme.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? light = null;
        string? dark = null;

        foreach (var part in parts)
        {
            // Find the first ':' or '='. Match Zig which tolerates spaces
            // around the separator (e.g. "dark : bar").
            var sepIdx = part.IndexOfAny([':', '=']);
            if (sepIdx <= 0) continue;

            var name = part[..sepIdx].Trim();
            var value = part[(sepIdx + 1)..].Trim();
            if (value.Length == 0) continue;

            if (string.Equals(name, "light", StringComparison.OrdinalIgnoreCase))
                light = value;
            else if (string.Equals(name, "dark", StringComparison.OrdinalIgnoreCase))
                dark = value;
        }

        // Zig's Theme.parseCLI requires both light and dark to be present;
        // a partial pair (e.g. "light:X" alone) is an error. Match that here.
        if (light is not null && dark is not null)
            return (light, dark);

        return (null, null);
    }

    /// <summary>
    /// Parse "palette = N=#RRGGBB" lines and write into the supplied
    /// 16-entry palette array. Lines that aren't palette entries are
    /// ignored, so this can be pointed at a full theme file or the
    /// user's main config file.
    /// </summary>
    public static void ApplyPaletteFromLines(IEnumerable<string> lines, uint[] palette)
    {
        if (palette.Length < 16)
            throw new ArgumentException("palette must have at least 16 entries", nameof(palette));

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0) continue;
            var key = trimmed[..eqIndex].Trim();
            if (key != "palette") continue;

            ApplyPaletteEntry(trimmed[(eqIndex + 1)..].Trim(), palette);
        }
    }

    /// <summary>
    /// Apply palette entries given as raw "N=#RRGGBB" values (no
    /// leading "palette =" prefix). Use this when the caller already
    /// has the values pre-extracted from the config by key and would
    /// otherwise have to re-prefix them just to let this module
    /// re-match the key.
    /// </summary>
    public static void ApplyPaletteFromValues(IEnumerable<string> values, uint[] palette)
    {
        if (palette.Length < 16)
            throw new ArgumentException("palette must have at least 16 entries", nameof(palette));

        foreach (var value in values)
            ApplyPaletteEntry(value, palette);
    }

    private static void ApplyPaletteEntry(string entry, uint[] palette)
    {
        var entryEq = entry.IndexOf('=');
        if (entryEq < 0) return;
        var idxStr = entry[..entryEq].Trim();
        if (!int.TryParse(idxStr, out var parsedIdx)) return;
        if (parsedIdx is < 0 or >= 16) return;

        var colorStr = entry[(entryEq + 1)..].Trim();
        if (TryParseHexRgb(colorStr, out var packed))
            palette[parsedIdx] = packed;
    }

    /// <summary>
    /// Parse a hex color string (#RGB, #RRGGBB, or #AARRGGBB) into
    /// a packed 0x00RRGGBB uint. Returns false on invalid input.
    /// </summary>
    public static bool TryParseHexRgb(string value, out uint packed)
    {
        packed = 0;
        if (string.IsNullOrEmpty(value)) return false;
        var hex = value.AsSpan().TrimStart('#');

        // Uses byte.TryParse over Parse+catch because this runs on
        // every config reload for up to 16 palette entries plus fg/bg/
        // cursor; exception-as-control-flow would be wasteful, and
        // AOT exercises the exception machinery enough as it is.
        switch (hex.Length)
        {
            case 3:
                if (!TryParseHexNibble(hex[0], out var r3)) return false;
                if (!TryParseHexNibble(hex[1], out var g3)) return false;
                if (!TryParseHexNibble(hex[2], out var b3)) return false;
                packed = ((uint)r3 << 16) | ((uint)g3 << 8) | b3;
                return true;
            case 6:
                return TryParseRgbBytes(hex[..2], hex[2..4], hex[4..6], out packed);
            case 8:
                // #AARRGGBB -- drop the alpha.
                return TryParseRgbBytes(hex[2..4], hex[4..6], hex[6..8], out packed);
            default:
                return false;
        }
    }

    private static bool TryParseRgbBytes(
        ReadOnlySpan<char> r, ReadOnlySpan<char> g, ReadOnlySpan<char> b, out uint packed)
    {
        packed = 0;
        if (!byte.TryParse(r, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rb)) return false;
        if (!byte.TryParse(g, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gb)) return false;
        if (!byte.TryParse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bb)) return false;
        packed = ((uint)rb << 16) | ((uint)gb << 8) | bb;
        return true;
    }

    private static bool TryParseHexNibble(char c, out byte value)
    {
        // Expand a 3-digit hex shorthand (e.g. 'F' -> 0xFF) without
        // allocating the "FF" string the old code built.
        int v = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        if (v < 0) { value = 0; return false; }
        value = (byte)(v * 0x11);
        return true;
    }
}
