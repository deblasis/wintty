using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Turns the raw screen text read from a surface (the bottom viewport slice)
/// into display lines for a preview tile: drop trailing blank lines so the last
/// line is real content (the prompt / last output), keep the last
/// <paramref name="maxRows"/> lines, and right-clip each to <paramref name="maxCols"/>.
/// Pure; no FFI.
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
            var line = split[i];
            lines.Add(line.Length > maxCols ? line.Substring(0, maxCols) : line);
        }
        return lines;
    }
}
