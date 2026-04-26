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

    public static ProfileParseResult Parse(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);

        if (configText.Length > 0 && configText[0] == '\uFEFF')
            configText = configText.Substring(1);

        var groups = new Dictionary<string, Dictionary<string, string>>();
        var warnings = new List<string>();

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
                VisualsOrNull: visuals.HasAny ? visuals.Value : null);
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
        return new IconSpec.Path(value);
    }

    private static bool ParseBool(string? value)
        => value is not null
           && bool.TryParse(value, out var b)
           && b;

    private static double? ParseDouble(string? value)
    {
        if (value is null) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }
}
