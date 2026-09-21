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
    /// Every directory libghostty searches for a named theme, in its order:
    /// the user directories, then the bundled themes. A name in an earlier
    /// directory hides the same name in a later one, so a user's copy of a
    /// bundled theme is the one that loads.
    /// </summary>
    /// <param name="configDirectory">See <see cref="UserDirectories"/>.</param>
    /// <param name="appData">See <see cref="UserDirectories"/>.</param>
    /// <param name="bundledDirectory">
    /// <see cref="BundledDirectory(string?, string?)"/>'s answer, or null.
    /// </param>
    public static IEnumerable<string> Directories(
        string? configDirectory, string? appData, string? bundledDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in UserDirectories(configDirectory, appData))
        {
            if (seen.Add(dir)) yield return dir;
        }
        if (!string.IsNullOrEmpty(bundledDirectory) && seen.Add(bundledDirectory))
            yield return bundledDirectory;
    }

    /// <summary>
    /// The directory holding the themes that ship with the app, as theme.zig's
    /// resources location resolves it on Windows, or null when there is none.
    /// </summary>
    /// <param name="resourcesDirectory">
    /// The GHOSTTY_RESOURCES_DIR environment value. libghostty takes it as the
    /// resources directory when it names an existing absolute directory, and
    /// then looks for themes in its <c>themes</c> subdirectory and nowhere
    /// else, whether or not that subdirectory exists.
    /// </param>
    /// <param name="appDirectory">
    /// The directory holding the executable. Without a resources directory,
    /// theme.zig looks for <c>share\ghostty\themes</c> beside the executable
    /// and in one directory above it, which is where the build copies the
    /// bundled themes.
    /// </param>
    /// <remarks>
    /// The resources directory is also found by climbing from the executable
    /// to a <c>share\terminfo\ghostty.terminfo</c>. The app now ships that
    /// file, so that detection does succeed; it is not mirrored here because
    /// it resolves to the same <c>share\ghostty\themes</c> beside the
    /// executable that the fallback below already returns.
    /// </remarks>
    public static string? BundledDirectory(string? resourcesDirectory, string? appDirectory)
        => BundledDirectory(resourcesDirectory, appDirectory, Directory.Exists, File.Exists);

    /// <summary>
    /// <see cref="BundledDirectory(string?, string?)"/> over injected
    /// existence checks, so the rule is testable without a disk.
    /// </summary>
    public static string? BundledDirectory(
        string? resourcesDirectory,
        string? appDirectory,
        Func<string, bool> directoryExists,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        fileExists ??= File.Exists;

        // resourcesdir.zig's validResourcesDir: non-empty, absolute, a
        // directory that opens, and on Windows carrying the same terminfo
        // sentinel detection requires. A value failing any of those is ignored
        // with a warning.
        //
        // The sentinel is mirrored rather than skipped as a lesser concern: a
        // theme file is parsed by the same code as a config file, underneath
        // the user's own config, so it carries every key the user has not set
        // and is not a list of colours. This method also documents itself as
        // following that rule, and a mirror that quietly stopped matching
        // would be worse than either behaviour on its own.
        if (!string.IsNullOrEmpty(resourcesDirectory)
            && IsAbsolute(resourcesDirectory)
            && directoryExists(resourcesDirectory)
            && HasTerminfoSentinel(resourcesDirectory, fileExists))
        {
            return Path.Combine(resourcesDirectory, "themes");
        }

        if (string.IsNullOrEmpty(appDirectory)) return null;

        // theme.zig's bundledThemesDir looks in the executable's own directory
        // and one above it, and stops there. One above is for the CLI, which
        // ships in bin and would otherwise find no bundled themes at all; the
        // stop is because nothing further up is any part of the install.
        var start = Path.TrimEndingDirectorySeparator(appDirectory);
        for (var climbed = 0; climbed < BundledThemesMaxAncestors; climbed++)
        {
            // A path with no parent is a drive root, which grants Authenticated
            // Users the right to create folders. resourcesdir.zig refuses to
            // probe one and so does the fallback this mirrors.
            var parent = Path.GetDirectoryName(start);
            if (string.IsNullOrEmpty(parent)) break;

            var candidate = Path.Combine(start, "share", "ghostty", "themes");
            if (directoryExists(candidate)) return candidate;

            start = parent;
        }

        return null;
    }

    /// <summary>
    /// How many directories <see cref="BundledDirectory(string?, string?)"/>
    /// looks in, counting the executable's own. Mirrors
    /// <c>bundled_themes_max_ancestors</c> in src/config/theme.zig.
    /// </summary>
    private const int BundledThemesMaxAncestors = 2;

    /// <summary>
    /// True when the terminfo sentinel sits beside <paramref name="resourcesDirectory"/>
    /// the way an install lays it out, i.e. <c>&lt;parent&gt;\terminfo\ghostty.terminfo</c>
    /// for a directory of <c>&lt;parent&gt;\ghostty</c>.
    /// </summary>
    private static bool HasTerminfoSentinel(
        string resourcesDirectory, Func<string, bool> fileExists)
    {
        var parent = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(resourcesDirectory));
        if (string.IsNullOrEmpty(parent)) return false;

        return fileExists(Path.Combine(parent, "terminfo", "ghostty.terminfo"));
    }

    /// <summary>
    /// <see cref="BundledDirectory(string?, string?)"/> for this process: its
    /// environment and the directory the app runs from.
    /// </summary>
    public static string? BundledDirectoryForThisProcess()
        => BundledDirectory(
            Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR"),
            AppContext.BaseDirectory);

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
