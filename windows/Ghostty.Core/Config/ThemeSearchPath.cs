using System;
using System.Collections.Generic;
using System.IO;

namespace Ghostty.Core.Config;

/// <summary>
/// Where to look for a user theme file, and which names are allowed.
///
/// This mirrors src/config/theme.zig. The two read the same theme for
/// different halves of one window -- libghostty renders the terminal from
/// it, the Windows shell reads it for the chrome around it -- so any
/// disagreement shows as a pane framed in a different palette than it is
/// filled with. Kept as a separate, testable piece because the rule has
/// drifted between the two sides before.
/// </summary>
/// <remarks>
/// Duplicated rather than shared: libghostty exports no theme-path call
/// today. An export wrapping theme.zig's own lookup would remove this
/// file and the whole class of drift with it.
/// </remarks>
public static class ThemeSearchPath
{
    /// <summary>
    /// Directories to search, most current first, without duplicates.
    /// </summary>
    /// <param name="configDirectory">
    /// Directory holding the config file libghostty resolved for editing.
    /// Its parent is the config root, which is where the sibling names
    /// below are looked for -- that root already accounts for
    /// XDG_CONFIG_HOME and for an APPDATA a portable launcher redirected,
    /// neither of which <paramref name="appData"/> can see.
    /// </param>
    /// <param name="appData">
    /// Roaming application data directory, used only when there is no
    /// config directory to derive a root from.
    /// </param>
    public static IEnumerable<string> UserDirectories(string? configDirectory, string? appData)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? configRoot = null;
        if (!string.IsNullOrEmpty(configDirectory))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(configDirectory);
            configRoot = Path.GetDirectoryName(trimmed);

            var fromConfig = Path.Combine(trimmed, "themes");
            if (seen.Add(fromConfig)) yield return fromConfig;
        }

        // Both application directory names under the config root, current
        // first, matching theme.zig's two xdgThemesDir calls. An install
        // can hold its config under one name and its themes under the
        // other, so the sibling is not reachable from configDirectory
        // alone. Falling back to appData only when there is no root to
        // derive keeps this from probing directories libghostty would
        // never look at.
        // GetDirectoryName gives null for a root and "" for a bare
        // segment; neither is a usable root, so both fall back.
        var root = string.IsNullOrEmpty(configRoot) ? appData : configRoot;
        if (string.IsNullOrEmpty(root)) yield break;

        foreach (var app in AppDirectoryNames)
        {
            var dir = Path.Combine(root, app, "themes");
            if (seen.Add(dir)) yield return dir;
        }
    }

    private static readonly string[] AppDirectoryNames = ["wintty", "ghostty"];

    /// <summary>
    /// True when a theme value is a bare file name to look up in the
    /// search directories. False for an absolute path, which is used
    /// as-is, and for a relative name with a directory component, which
    /// theme.zig refuses outright with a diagnostic -- resolving one here
    /// would load a theme the terminal never applied.
    /// </summary>
    public static bool IsSearchableName(string themeName)
        => !string.IsNullOrEmpty(themeName)
           && !IsAbsolute(themeName)
           && string.Equals(themeName, Path.GetFileName(themeName), StringComparison.Ordinal);

    /// <summary>
    /// True when a theme value is an absolute path, by the same rule
    /// std.fs.path.isAbsoluteWindows uses: a leading separator, or a drive
    /// letter followed by one.
    /// </summary>
    /// <remarks>
    /// Path.IsPathRooted alone is wider than that rule. It also accepts
    /// the drive-relative form with no separator, <c>C:mocha</c>, which
    /// libghostty treats as a plain name, finds a directory component in,
    /// and rejects. Taking it as absolute here would theme the chrome from
    /// a file the terminal refused.
    /// </remarks>
    public static bool IsAbsolute(string themeName)
    {
        if (string.IsNullOrEmpty(themeName)) return false;
        if (themeName[0] is '\\' or '/') return true;
        return Path.IsPathFullyQualified(themeName);
    }
}
