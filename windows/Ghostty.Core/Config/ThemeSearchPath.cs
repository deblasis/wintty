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
    /// Directory holding the config file libghostty actually loaded.
    /// Probed first because libghostty resolved it, so it already accounts
    /// for XDG_CONFIG_HOME and for an APPDATA that a portable launcher has
    /// redirected -- neither of which the fallbacks below can see.
    /// </param>
    /// <param name="appData">Roaming application data directory.</param>
    public static IEnumerable<string> UserDirectories(string? configDirectory, string? appData)
    {
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(configDirectory))
        {
            var fromConfig = Path.Combine(configDirectory, "themes");
            if (seen.Add(fromConfig)) yield return fromConfig;
        }

        if (string.IsNullOrEmpty(appData)) yield break;

        // The two application directory names, current first, matching the
        // `user` and `user_ghostty` entries of theme.zig's Location enum.
        // An install can hold its config under one name and its themes
        // under the other, so neither is reachable from configDirectory
        // alone.
        foreach (var app in new[] { "wintty", "ghostty" })
        {
            var dir = Path.Combine(appData, app, "themes");
            if (seen.Add(dir)) yield return dir;
        }
    }

    /// <summary>
    /// True when a theme value names a file to look up in the search
    /// directories, rather than an absolute path or something with a
    /// directory component.
    /// </summary>
    /// <remarks>
    /// theme.zig rejects a relative name containing a path separator
    /// outright, with a diagnostic. Accepting one here would resolve a
    /// theme libghostty refuses, which is the same disagreement by another
    /// route -- the terminal falls back to defaults while the chrome loads
    /// whatever the traversal reached.
    /// </remarks>
    public static bool IsSearchableName(string themeName)
        => !string.IsNullOrEmpty(themeName)
           && !Path.IsPathRooted(themeName)
           && string.Equals(themeName, Path.GetFileName(themeName), System.StringComparison.Ordinal);
}
