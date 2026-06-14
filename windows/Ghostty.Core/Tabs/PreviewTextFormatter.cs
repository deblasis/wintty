using System;
using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Turns the raw screen text read from a surface (the whole viewport) into
/// display lines for a preview tile: drop trailing blank lines so the last line
/// is real content (the prompt / last output), keep the last
/// <paramref name="maxRows"/> lines, sanitize unrenderable glyphs, and right-clip
/// each to <paramref name="maxCols"/>. Pure; no FFI.
/// </summary>
internal static class PreviewTextFormatter
{
    public static IReadOnlyList<string> Format(string? raw, int maxRows, int maxCols)
    {
        if (string.IsNullOrEmpty(raw) || maxRows <= 0 || maxCols <= 0)
            return Array.Empty<string>();

        var split = raw.Replace("\r", string.Empty).Split('\n');

        // Find the last non-blank line; everything after it is trailing blank.
        var end = split.Length;
        while (end > 0 && string.IsNullOrWhiteSpace(split[end - 1])) end--;
        if (end == 0) return Array.Empty<string>();

        var start = Math.Max(0, end - maxRows);
        var lines = new List<string>(end - start);
        for (var i = start; i < end; i++)
        {
            var line = Sanitize(split[i]);
            lines.Add(line.Length > maxCols ? line.Substring(0, maxCols) : line);
        }
        return lines;
    }

    // Drop characters a plain monospace TextBlock can't render usefully: control
    // codes, and private-use-area codepoints (where powerline / nerd-font prompt
    // glyphs live - they show as tofu boxes otherwise). Keeps the preview
    // readable as the last few commands rather than a row of squares.
    private static string Sanitize(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (char.IsControl(c)) continue;
            if (c >= 0xE000 && c <= 0xF8FF) continue; // BMP private use area
            sb.Append(c);
        }
        // Collapse trailing whitespace a stripped glyph can leave behind.
        return sb.ToString().TrimEnd();
    }
}
