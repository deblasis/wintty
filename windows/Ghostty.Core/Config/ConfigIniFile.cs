using System;
using System.Collections.Generic;
using System.IO;

namespace Ghostty.Core.Config;

/// <summary>
/// Reader for a ghostty-style ini file. Keys that libghostty's parser does
/// not know about (see <see cref="WindowsOnlyKeys"/>) cannot be read through
/// <c>ghostty_config_get</c>, so the Windows side parses the file itself.
/// </summary>
/// <remarks>
/// Shared rather than private to the config service because one of these keys
/// is read before that service can exist: the single-instance election runs
/// ahead of <c>Application.Start</c>.
/// </remarks>
public static class ConfigIniFile
{
    /// <summary>
    /// Load <paramref name="path"/> into a key/value dictionary. Empty lines
    /// and #-prefixed comments are skipped, empty values are ignored entirely,
    /// and keys are matched case-insensitively. Values may themselves contain
    /// <c>=</c>; only the first one separates.
    /// </summary>
    /// <remarks>
    /// Returns an empty dictionary for a path that does not exist -- and for
    /// one whose existence cannot be established, since the probe reports a
    /// denied file as missing. A file that exists but cannot be read, an
    /// editor holding it exclusively being the usual case, propagates the I/O
    /// failure instead. Callers decide what that means: the config service
    /// lets it escape (a half-read config is worse than none), the pre-startup
    /// election degrades to the default.
    /// </remarks>
    public static Dictionary<string, List<string>> Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // FileShare.ReadWrite rather than File.ReadLines' default of
        // FileShare.Read. This file has writers: the settings UI rewrites it,
        // and libghostty holds a write handle across its own config edits. A
        // reader that refuses to share writes turns any of those into a
        // sharing violation on a file that is merely open, not locked.
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return Parse(ReadLines(reader));
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>
    /// Parse ini text that is already in memory, by the same rules as
    /// <see cref="Load"/>. Used for the built-in theme libghostty hands back
    /// as a string rather than a file.
    /// </summary>
    public static Dictionary<string, List<string>> ParseText(string? text)
        => string.IsNullOrEmpty(text)
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : Parse(text.Split('\n'));

    private static Dictionary<string, List<string>> Parse(IEnumerable<string> lines)
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0) continue;
            var k = trimmed[..eqIndex].Trim();
            if (k.Length == 0) continue;
            var v = trimmed[(eqIndex + 1)..].Trim();
            if (v.Length == 0) continue;
            if (!dict.TryGetValue(k, out var list))
            {
                list = new List<string>(1);
                dict[k] = list;
            }
            list.Add(v);
        }
        return dict;
    }

    /// <summary>
    /// First value recorded for <paramref name="key"/>, or
    /// <paramref name="defaultValue"/> when the file does not set it.
    /// </summary>
    public static string First(
        IReadOnlyDictionary<string, List<string>>? file,
        string key,
        string defaultValue = "")
        => file is not null
            && file.TryGetValue(key, out var list)
            && list.Count > 0
            ? list[0]
            : defaultValue;
}
