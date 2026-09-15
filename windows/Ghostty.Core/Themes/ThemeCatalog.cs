using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ghostty.Core.Config;

namespace Ghostty.Core.Themes;

/// <summary>
/// The themes a user can pick, as one list every picker in the shell reads:
/// the Settings Colors page and the command palette's theme mode.
///
/// The directories are <see cref="ThemeSearchPath.Directories"/>, the C#
/// mirror of the lookup theme.zig uses to resolve a configured theme: the
/// user's theme directories, then the themes bundled with the app. A name
/// offered here is therefore a name libghostty will find when it is written
/// to the config, and a user's copy of a bundled theme is listed once, as
/// the file that loads.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>
    /// Every theme file in <paramref name="directories"/>, in search order: a
    /// name found in an earlier directory hides the same name in a later one,
    /// which is the file libghostty would load for it. Sorted by name,
    /// case-insensitively, the way +list-themes and the Settings page show them.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(IEnumerable<string> directories)
        => Enumerate(directories, ListFiles);

    /// <summary>
    /// <see cref="Enumerate(IEnumerable{string})"/> over an injected listing,
    /// so the ordering and shadowing rules are testable without a disk.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(
        IEnumerable<string> directories,
        Func<string, IEnumerable<string>> listFileNames)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(listFileNames);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var directory in directories)
        {
            foreach (var name in listFileNames(directory))
            {
                // .DS_Store is the one file +list-themes skips by name.
                if (name == ".DS_Store") continue;
                if (!IsPersistableName(name)) continue;
                if (seen.Add(name)) names.Add(name);
            }
        }

        names.Sort(static (a, b) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(a, b);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a, b);
        });
        return names;
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be written as <c>theme = name</c>
    /// and read back as that one theme. A comma or an equals sign would be
    /// read as a light/dark pair, a quote as a quoted value, and surrounding
    /// whitespace is trimmed by the parser, so none of those names could be
    /// persisted as what the user picked.
    /// </summary>
    public static bool IsPersistableName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return false;
        if (!ThemeSearchPath.IsSearchableName(name)) return false;
        foreach (var c in name)
        {
            if (c is ',' or '=' or '"' || char.IsControl(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// The themes whose name contains <paramref name="query"/>,
    /// case-insensitively (the palette's command search matches the same
    /// way), best match first: the name itself, then names that start with
    /// the query, then names with a word that does, then the rest. Within
    /// each of those, catalog order. An empty query is every theme, in
    /// catalog order.
    /// </summary>
    /// <remarks>
    /// Best first because the highlight lands on the top row and the preview
    /// follows the highlight: typing "nord" shows Nord, not the first name
    /// that happens to contain it alphabetically.
    /// </remarks>
    public static IReadOnlyList<string> Filter(IReadOnlyList<string> themes, string? query)
    {
        ArgumentNullException.ThrowIfNull(themes);
        if (string.IsNullOrWhiteSpace(query)) return themes;
        var trimmed = query.Trim();
        return themes
            .Select((name, index) => (name, index, rank: MatchRank(name, trimmed)))
            .Where(m => m.rank != MatchNone)
            // OrderBy is stable, and the index keeps catalog order explicit.
            .OrderBy(m => m.rank)
            .ThenBy(m => m.index)
            .Select(m => m.name)
            .ToList();
    }

    /// <summary>The rank <see cref="MatchRank"/> gives a name that does not match.</summary>
    public const int MatchNone = int.MaxValue;

    /// <summary>
    /// How well <paramref name="name"/> matches <paramref name="query"/>,
    /// case-insensitively, lower being better: 0 the whole name, 1 its start,
    /// 2 the start of a later word (after a character that is not a letter or
    /// a digit), 3 anywhere else, <see cref="MatchNone"/> nowhere.
    /// </summary>
    public static int MatchRank(string name, string query)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return 3;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;

        var best = MatchNone;
        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var rank = at == 0 ? 1 : !char.IsLetterOrDigit(name[at - 1]) ? 2 : 3;
            if (rank < best) best = rank;
            if (best == 1 || at + 1 >= name.Length) break;
            at = name.IndexOf(query, at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return best;
    }

    /// <summary>
    /// Where the highlight starts when the list opens: the theme on screen,
    /// when it is in the list, and otherwise nowhere in particular (the caller
    /// takes the top). Matched case-insensitively because theme.zig finds a
    /// theme file on a case-insensitive file system either way.
    /// </summary>
    public static string? InitialSelection(IReadOnlyList<string> themes, string? activeTheme)
    {
        ArgumentNullException.ThrowIfNull(themes);
        if (string.IsNullOrEmpty(activeTheme)) return null;
        foreach (var theme in themes)
        {
            if (string.Equals(theme, activeTheme, StringComparison.OrdinalIgnoreCase))
                return theme;
        }
        return null;
    }

    private static IEnumerable<string> ListFiles(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable directory is a directory with no themes in it,
            // which is also what libghostty's walk makes of it.
            return Array.Empty<string>();
        }
    }
}
