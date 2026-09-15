using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ghostty.Core.Themes;

/// <summary>
/// The palette's theme swatches, read on first use and kept for the rest of
/// the browse. A long theme list only ever reads the files of the rows it
/// realizes, and a row that scrolls back into view, or survives a filter
/// keystroke, is painted from here without touching the disk again.
///
/// A theme whose file is missing or unreadable is remembered as null, so a
/// broken entry costs one probe, not one per realization.
///
/// Not thread-safe: the palette reads it from the UI thread only.
/// </summary>
public sealed class ThemeSwatchCache
{
    /// <summary>
    /// The most lines read from one theme file. A real theme is a few dozen
    /// lines; the cap keeps a stray large file in a themes directory from
    /// stalling the list.
    /// </summary>
    public const int MaxLines = 1024;

    private readonly Func<string, IEnumerable<string>?> _readTheme;
    private readonly Dictionary<string, ThemeSwatch?> _swatches = new(StringComparer.Ordinal);

    /// <param name="readTheme">
    /// The lines of the named theme's file, or null when there is none.
    /// </param>
    public ThemeSwatchCache(Func<string, IEnumerable<string>?> readTheme)
    {
        ArgumentNullException.ThrowIfNull(readTheme);
        _readTheme = readTheme;
    }

    /// <summary>
    /// A cache over the theme directories <paramref name="directories"/>
    /// yields, searched in order: the file libghostty would load for a name
    /// is the one its swatch is drawn from.
    /// </summary>
    public static ThemeSwatchCache ForDirectories(Func<IEnumerable<string>> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return new ThemeSwatchCache(name => ReadFirst(directories(), name));
    }

    /// <summary>How many themes have been read (or found missing) so far.</summary>
    public int Count => _swatches.Count;

    /// <summary>
    /// The swatch for <paramref name="themeName"/> if it has already been
    /// read, without reading anything. True when the answer is known, which
    /// includes knowing the theme has no readable file (a null swatch).
    /// </summary>
    public bool TryGetCached(string themeName, out ThemeSwatch? swatch)
        => _swatches.TryGetValue(themeName, out swatch);

    /// <summary>
    /// The swatch for <paramref name="themeName"/>, reading its file the first
    /// time. Null when the name is not one the config can carry or the file is
    /// missing or unreadable.
    /// </summary>
    public ThemeSwatch? Get(string themeName)
    {
        ArgumentNullException.ThrowIfNull(themeName);
        if (_swatches.TryGetValue(themeName, out var known)) return known;

        ThemeSwatch? swatch = null;
        if (ThemeCatalog.IsPersistableName(themeName))
        {
            try
            {
                if (_readTheme(themeName) is { } lines)
                    swatch = ThemeSwatch.Parse(lines.Take(MaxLines));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is drawn as a blank swatch, like a missing file.
            }
        }

        _swatches[themeName] = swatch;
        return swatch;
    }

    /// <summary>Forget every swatch, so the next browse reads the files again.</summary>
    public void Clear() => _swatches.Clear();

    private static IEnumerable<string>? ReadFirst(IEnumerable<string> directories, string themeName)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory)) continue;
            var path = Path.Combine(directory, themeName);
            if (File.Exists(path)) return File.ReadLines(path);
        }
        return null;
    }
}
