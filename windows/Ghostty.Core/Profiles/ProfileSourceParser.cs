using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Parsed result: the dictionary of profile defs by ID, plus any
/// non-fatal warnings collected during parsing. Fatal errors (e.g.
/// missing required keys) cause the offending profile to be omitted.
/// </summary>
public sealed record ProfileParseResult(
    IReadOnlyDictionary<string, ProfileDef> Profiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pure function: extract per-profile keys from raw config text.
/// Lines matching <c>profile.&lt;id&gt;.&lt;subkey&gt; = &lt;value&gt;</c>
/// are collected; everything else is ignored. ID format is
/// kebab-case ASCII (<c>[a-z0-9-]+</c>); invalid IDs cause the profile to
/// be dropped with a warning.
/// </summary>
public static partial class ProfileSourceParser
{
    [GeneratedRegex(
        @"^profile\.([a-z0-9-]+)\.([a-z0-9-]+)\s*=\s*(.+?)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();

    /// <summary>
    /// The key half of <see cref="LineRegex"/> (the "profile.&lt;id&gt;.&lt;subkey&gt;"
    /// shape, with no "= value" suffix), for the overloads that start from an
    /// already-split key/value cache (<see cref="ConfigIniFile"/>'s
    /// dictionary) instead of raw file text. Same character classes and the
    /// same <see cref="RegexOptions.IgnoreCase"/>, so a key this rejects is a
    /// key <see cref="LineRegex"/> would also have rejected, and vice versa.
    /// </summary>
    [GeneratedRegex(
        @"^profile\.([a-z0-9-]+)\.([a-z0-9-]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();

    /// <summary>
    /// Has no production caller: the profile pass reads the ini cache via
    /// <see cref="Parse(IReadOnlyDictionary{string, List{string}})"/>
    /// instead. Kept as the reference implementation the pairs overload is
    /// tested against (equivalence tests) and as the simpler fixture API
    /// for tests that don't need a config-file cache.
    /// </summary>
    public static ProfileParseResult Parse(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);

        if (configText.Length > 0 && configText[0] == '\uFEFF')
            configText = configText.Substring(1);

        var groups = new Dictionary<string, Dictionary<string, string>>();

        foreach (var rawLine in configText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var match = LineRegex().Match(line);
            if (!match.Success) continue;

            var id = match.Groups[1].Value.ToLowerInvariant();
            var subKey = match.Groups[2].Value.ToLowerInvariant();
            var value = match.Groups[3].Value;

            if (!groups.TryGetValue(id, out var bag))
                groups[id] = bag = new Dictionary<string, string>();
            bag[subKey] = value;
        }

        return BuildProfiles(groups);
    }

    /// <summary>
    /// Same result as <see cref="Parse(string)"/>, built from the parsed-pairs
    /// cache <see cref="ConfigIniFile.Load"/> already produced, instead of a
    /// second raw-text read of the same file.
    ///
    /// <paramref name="configPairs"/> is keyed by the untouched "key" half of
    /// each "key = value" line (whatever casing the file used), with every
    /// occurrence's value in file order -- the shape
    /// <see cref="ConfigIniFile.Load"/> already returns. Each key is matched
    /// against <see cref="KeyRegex"/> exactly the way <see cref="LineRegex"/>
    /// would match the key half of the original line, so a line this drops
    /// is a line <see cref="Parse(string)"/> would also have dropped. Last
    /// value wins per (id, subkey) pair, matching the forward-order
    /// <c>bag[subKey] = value</c> overwrite in the text path -- which is
    /// exactly the cache list's last element, since
    /// <see cref="ConfigIniFile.Load"/> appends every occurrence of a key in
    /// the order it read them.
    ///
    /// A whitespace-only value, e.g. "profile.x.name =    ", is dropped
    /// identically by both paths: <see cref="Parse(string)"/> trims the
    /// whole line first, leaving "profile.x.name =" with nothing left for
    /// <see cref="LineRegex"/> to capture, the same way <see cref="ConfigIniFile.Load"/>
    /// skips a key whose value is empty after trimming.
    /// </summary>
    public static ProfileParseResult Parse(IReadOnlyDictionary<string, List<string>> configPairs)
    {
        ArgumentNullException.ThrowIfNull(configPairs);

        var groups = new Dictionary<string, Dictionary<string, string>>();

        foreach (var (rawKey, values) in configPairs)
        {
            if (values.Count == 0) continue;

            var match = KeyRegex().Match(rawKey);
            if (!match.Success) continue;

            var id = match.Groups[1].Value.ToLowerInvariant();
            var subKey = match.Groups[2].Value.ToLowerInvariant();
            // Last occurrence wins, same as the forward-order overwrite in
            // Parse(string) above -- the cache list is already in file order.
            var value = values[^1];

            if (!groups.TryGetValue(id, out var bag))
                groups[id] = bag = new Dictionary<string, string>();
            bag[subKey] = value;
        }

        return BuildProfiles(groups);
    }

    private static ProfileParseResult BuildProfiles(
        Dictionary<string, Dictionary<string, string>> groups)
    {
        var warnings = new List<string>();
        var profiles = new Dictionary<string, ProfileDef>();
        foreach (var (id, bag) in groups)
        {
            if (!bag.TryGetValue("name", out var name) || name.Length == 0)
            {
                warnings.Add($"profile '{id}': missing required key 'name', dropped");
                continue;
            }
            if (!bag.TryGetValue("command", out var command) || command.Length == 0)
            {
                warnings.Add($"profile '{id}': missing required key 'command', dropped");
                continue;
            }

            var visuals = BuildVisuals(bag);
            profiles[id] = new ProfileDef(
                Id: id,
                Name: name,
                Command: command,
                WorkingDirectory: bag.GetValueOrDefault("working-directory"),
                Icon: ParseIcon(bag.GetValueOrDefault("icon")),
                TabTitle: bag.GetValueOrDefault("tab-title"),
                Hidden: ParseBool(bag.GetValueOrDefault("hidden")),
                ProbeId: null,
                VisualsOrNull: visuals.HasAny ? visuals.Value : null,
                // Default true so profiles without an explicit override
                // still participate in active-process icon tracking.
                // Malformed values silently retain the default, matching
                // how the existing parser treats other bad values rather
                // than failing the profile load.
                TabIconTracksForeground: ParseBoolOrDefault(
                    bag.GetValueOrDefault("tab-icon-tracks-foreground"),
                    defaultValue: true));
        }

        return new ProfileParseResult(profiles, warnings);
    }

    /// <summary>
    /// Extracts ids for which <c>profile.&lt;id&gt;.hidden = true</c>
    /// appears. Matches the same id format as <see cref="Parse"/>
    /// (lowercase ASCII). Ignores <c>hidden = false</c> and any other
    /// subkey. Used by <c>ProfileRegistry</c> to suppress discovered
    /// profiles without requiring a full user override.
    /// </summary>
    public static IReadOnlySet<string> ExtractHiddenIds(string configText)
        => ExtractHiddenIdsCore(configText, requireTrue: true);

    /// <summary>
    /// Extracts ids for which any <c>profile.&lt;id&gt;.hidden = ...</c>
    /// line appears, regardless of whether the value parses as true or
    /// false. Used by the warnings filter so an id whose only
    /// <c>profile.&lt;id&gt;.*</c> line is a hide-override (true OR
    /// false) doesn't get a spurious "missing required key 'name'"
    /// warning. The true-only set isn't sufficient because the settings
    /// page's un-hide path can leave <c>hidden = false</c> markers in
    /// the config, and an explicit <c>hidden = false</c> mention is
    /// still a hide-override marker, not a malformed profile block.
    /// </summary>
    public static IReadOnlySet<string> ExtractHiddenMentionIds(string configText)
        => ExtractHiddenIdsCore(configText, requireTrue: false);

    /// <summary>
    /// Pairs-cache counterpart of <see cref="ExtractHiddenIds(string)"/>. See
    /// <see cref="Parse(IReadOnlyDictionary{string, List{string}})"/> for the
    /// shape <paramref name="configPairs"/> is expected in.
    ///
    /// The text path adds an id as soon as any one occurrence of its
    /// <c>hidden</c> line parses to <see langword="true"/> -- later
    /// occurrences overwriting it back to <see langword="false"/> do not
    /// remove it, since <c>ids</c> is a set with no un-add. So this checks
    /// whether *any* cached value for that key parses to <see langword="true"/>,
    /// which is the same test applied to every occurrence instead of just the
    /// last one.
    /// </summary>
    public static IReadOnlySet<string> ExtractHiddenIds(IReadOnlyDictionary<string, List<string>> configPairs)
        => ExtractHiddenIdsCore(configPairs, requireTrue: true);

    /// <summary>Pairs-cache counterpart of <see cref="ExtractHiddenMentionIds(string)"/>.</summary>
    public static IReadOnlySet<string> ExtractHiddenMentionIds(IReadOnlyDictionary<string, List<string>> configPairs)
        => ExtractHiddenIdsCore(configPairs, requireTrue: false);

    private static IReadOnlySet<string> ExtractHiddenIdsCore(string configText, bool requireTrue)
    {
        ArgumentNullException.ThrowIfNull(configText);

        if (configText.Length > 0 && configText[0] == '\uFEFF')
            configText = configText.Substring(1);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in configText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var match = LineRegex().Match(line);
            if (!match.Success) continue;

            var subKey = match.Groups[2].Value;
            if (!string.Equals(subKey, "hidden", StringComparison.OrdinalIgnoreCase))
                continue;

            if (requireTrue)
            {
                var value = match.Groups[3].Value;
                if (!bool.TryParse(value, out var flag) || !flag) continue;
            }

            var id = match.Groups[1].Value.ToLowerInvariant();
            ids.Add(id);
        }

        return ids;
    }

    private static IReadOnlySet<string> ExtractHiddenIdsCore(
        IReadOnlyDictionary<string, List<string>> configPairs, bool requireTrue)
    {
        ArgumentNullException.ThrowIfNull(configPairs);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, values) in configPairs)
        {
            if (values.Count == 0) continue;

            var match = KeyRegex().Match(rawKey);
            if (!match.Success) continue;

            var subKey = match.Groups[2].Value;
            if (!string.Equals(subKey, "hidden", StringComparison.OrdinalIgnoreCase))
                continue;

            if (requireTrue)
            {
                var anyTrue = false;
                foreach (var value in values)
                {
                    if (bool.TryParse(value, out var flag) && flag)
                    {
                        anyTrue = true;
                        break;
                    }
                }
                if (!anyTrue) continue;
            }

            var id = match.Groups[1].Value.ToLowerInvariant();
            ids.Add(id);
        }

        return ids;
    }

    private static (EffectiveVisualOverrides Value, bool HasAny) BuildVisuals(
        Dictionary<string, string> bag)
    {
        var theme = bag.GetValueOrDefault("theme");
        var opacity = ParseDouble(bag.GetValueOrDefault("background-opacity"));
        var fontFamily = bag.GetValueOrDefault("font-family");
        var fontSize = ParseDouble(bag.GetValueOrDefault("font-size"));
        var cursorStyle = bag.GetValueOrDefault("cursor-style");

        var hasAny = theme is not null
                     || opacity is not null
                     || fontFamily is not null
                     || fontSize is not null
                     || cursorStyle is not null;

        return (new EffectiveVisualOverrides(theme, opacity, fontFamily, fontSize, cursorStyle), hasAny);
    }

    private static IconSpec? ParseIcon(string? value)
    {
        if (value is null) return null;
        if (value.StartsWith("mdl2:", System.StringComparison.OrdinalIgnoreCase))
        {
            var hex = value.Substring(5);
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                return new IconSpec.Mdl2Token(cp);
            return null;
        }
        if (value.StartsWith("brand:", System.StringComparison.OrdinalIgnoreCase))
        {
            var key = value.Substring(6);
            if (key.Length == 0) return new IconSpec.Path(value);
            return new IconSpec.BrandKey(key, null);
        }
        return new IconSpec.Path(value);
    }

    private static bool ParseBool(string? value)
        => value is not null
           && bool.TryParse(value, out var b)
           && b;

    // Trinary variant: distinguishes absent / malformed (use default)
    // from explicit true / false. ParseBool collapses everything that
    // isn't "true" into false, which is wrong for keys whose default
    // is true (e.g. tab-icon-tracks-foreground).
    private static bool ParseBoolOrDefault(string? value, bool defaultValue)
        => value is not null && bool.TryParse(value, out var b)
            ? b
            : defaultValue;

    private static double? ParseDouble(string? value)
    {
        if (value is null) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }
}
