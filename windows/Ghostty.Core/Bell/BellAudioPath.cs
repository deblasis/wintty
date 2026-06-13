using System;
using System.IO;

namespace Ghostty.Core.Bell;

/// <summary>
/// Resolves the <c>bell-audio-path</c> config value to an absolute path.
/// The Zig config type is <c>?Path</c>, a tagged union that does not
/// round-trip cleanly through <c>ghostty_config_get</c>, so the Windows
/// side reads the raw string from the config file cache and resolves it
/// here, mirroring the Zig <c>Path</c> semantics: <c>~/</c> expands to the
/// home directory, rooted paths pass through, and relative paths resolve
/// against the directory of the config file that referenced them. When no
/// config directory is known, a relative path falls back to the process
/// working directory.
/// </summary>
public static class BellAudioPath
{
    public static string? Resolve(string? raw, string? configDir, string? homeDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = raw.Trim();

        if ((path.StartsWith("~/", StringComparison.Ordinal)
                || path.StartsWith("~\\", StringComparison.Ordinal))
            && !string.IsNullOrEmpty(homeDir))
        {
            return Path.GetFullPath(Path.Combine(homeDir, path[2..]));
        }

        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

        return !string.IsNullOrEmpty(configDir)
            ? Path.GetFullPath(Path.Combine(configDir, path))
            : Path.GetFullPath(path);
    }
}
