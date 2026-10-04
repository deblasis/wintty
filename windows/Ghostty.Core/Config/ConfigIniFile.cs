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
    /// and #-prefixed comments are skipped, keys are matched
    /// case-insensitively, and each key's values are kept in file order. Values
    /// may themselves contain <c>=</c>; only the first one separates.
    /// <para>
    /// An empty value (<c>theme = </c>) is a RESET, not a value: it drops every
    /// earlier line for that key, leaving the key absent. That is what
    /// libghostty does with it -- an empty value assigns the field's default,
    /// and for a repeatable key the default is the empty list (Config.zig's
    /// parseIntoField, and the <c>key = ""</c> recipe it documents) -- and it
    /// is what the writer emits for a cleared key (<see cref="ConfigFileParser.SetValue"/>).
    /// Ignoring the blank instead, as this did, left the parse answering
    /// "A" for <c>theme = A</c> followed by <c>theme = </c>: the one shape a
    /// user writes to put a key back.
    /// </para>
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
            // A blank value resets the key, so anything the file set for it
            // before this line stops counting. Dropping the entry (rather
            // than recording the empty string) is what makes "the file clears
            // a key" and "the file never mentions the key" the same answer to
            // every reader below, which is what libghostty's own default
            // assignment makes them.
            if (v.Length == 0)
            {
                dict.Remove(k);
                continue;
            }
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
    /// <remarks>
    /// Kept for the readers whose key is genuinely first-wins, and pinned by
    /// its own tests so nobody re-points it at <see cref="Last"/> by accident.
    /// Config keys are last-wins (ghostty's own parser, and the writer in
    /// <see cref="ConfigFileParser"/>), which is what <see cref="Last"/>
    /// exists for.
    /// </remarks>
    public static string First(
        IReadOnlyDictionary<string, List<string>>? file,
        string key,
        string defaultValue = "")
        => file is not null
            && file.TryGetValue(key, out var list)
            && list.Count > 0
            ? list[0]
            : defaultValue;

    /// <summary>
    /// Value in force for <paramref name="key"/>, or
    /// <paramref name="defaultValue"/> when the file does not set it.
    /// </summary>
    /// <remarks>
    /// The last line wins, because that is what libghostty applies and what
    /// the settings UI writes: a config with two <c>theme</c> lines paints the
    /// panes from the second one, so a reader that answered with the first
    /// would theme the window next to a terminal wearing a different palette.
    /// Reading <c>First</c> here is the defect this exists to correct, and the
    /// tests pin both halves: duplicate lines resolve to the last, and a blank
    /// <c>key = </c> resolves to the default (see <see cref="Parse"/>).
    /// </remarks>
    public static string Last(
        IReadOnlyDictionary<string, List<string>>? file,
        string key,
        string defaultValue = "")
        => file is not null
            && file.TryGetValue(key, out var list)
            && list.Count > 0
            ? list[^1]
            : defaultValue;

    /// <summary>
    /// <see cref="Last"/> reporting whether the file sets the key at all,
    /// for the readers whose meaning depends on presence rather than on the
    /// value: an unset <c>frame-style</c> means "match the backdrop", and a
    /// default argument could not tell that apart from the user having
    /// written the default down.
    /// </summary>
    public static bool TryLast(
        IReadOnlyDictionary<string, List<string>>? file,
        string key,
        out string value)
    {
        if (file is not null
            && file.TryGetValue(key, out var list)
            && list.Count > 0)
        {
            value = list[^1];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
