using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Config;

/// <summary>
/// Pure helper invoked by <c>ConfigService.ReadFlagsCore</c> after the
/// raw file cache is populated. Returns the five profile-view values
/// exposed on <see cref="IConfigService"/> and
/// <see cref="IProfileConfigSource"/>. Kept separate from
/// <c>ConfigService</c> so the parse logic is unit-testable on Linux
/// without the WinUI + libghostty dependency chain.
/// </summary>
public static class ConfigServiceProfileParser
{
    /// <summary>
    /// <paramref name="configText"/> is the raw file contents. The
    /// <paramref name="fileValueReader"/> delegate is a thin adapter
    /// over <c>ConfigService</c>'s existing <c>GetFileValue</c>
    /// helper: it returns the last raw value for a key, or
    /// <see langword="null"/> when the key is absent.
    /// </summary>
    public static ProfileView ParseAll(
        string configText,
        Func<string, string?> fileValueReader)
    {
        ArgumentNullException.ThrowIfNull(configText);
        ArgumentNullException.ThrowIfNull(fileValueReader);

        var parsed = ProfileSourceParser.Parse(configText);
        var hidden = ProfileSourceParser.ExtractHiddenIds(configText);

        var defaultId = fileValueReader("default-profile");
        if (string.IsNullOrEmpty(defaultId)) defaultId = null;

        var profileOrderRaw = fileValueReader("profile-order") ?? string.Empty;
        var profileOrder = ParseCsv(profileOrderRaw);

        // Suppress warnings for ids that appear only as a hidden-override
        // (e.g. "profile.foo.hidden = true" with no name/command). These
        // are intentional suppression markers, not malformed definitions.
        var warnings = FilterHiddenOnlyWarnings(parsed.Warnings, parsed.Profiles, hidden);

        return new ProfileView(
            ParsedProfiles: parsed.Profiles,
            ProfileOrder: profileOrder,
            DefaultProfileId: defaultId,
            HiddenProfileIds: hidden,
            ProfileWarnings: warnings);
    }

    // Warnings for an id which is in the hidden set and absent from parsed
    // profiles are suppressed -- those entries are pure hide-overrides,
    // not broken definitions. Anchor on the exact "profile '<id>':" prefix
    // emitted by ProfileSourceParser so a hidden id that happens to be a
    // substring of another id's warning does not accidentally suppress it.
    private static IReadOnlyList<string> FilterHiddenOnlyWarnings(
        IReadOnlyList<string> warnings,
        IReadOnlyDictionary<string, ProfileDef> profiles,
        IReadOnlySet<string> hidden)
    {
        if (warnings.Count == 0) return warnings;

        // Precompute the "profile '<id>':" prefixes once per hidden id
        // rather than per (warning x id) pair; the old string interpolation
        // inside the inner loop allocated a new format string on every
        // iteration. Skip ids that also have a full parsed definition --
        // those warnings are for genuinely broken blocks, not hide-only
        // overrides.
        List<string>? prefixes = null;
        foreach (var id in hidden)
        {
            if (profiles.ContainsKey(id)) continue;
            (prefixes ??= new List<string>(hidden.Count)).Add($"profile '{id}':");
        }
        if (prefixes is null) return warnings;

        var result = new List<string>(warnings.Count);
        foreach (var w in warnings)
        {
            var suppressed = false;
            foreach (var prefix in prefixes)
            {
                if (w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    suppressed = true;
                    break;
                }
            }
            if (!suppressed) result.Add(w);
        }
        return result;
    }

    private static IReadOnlyList<string> ParseCsv(string input)
    {
        if (input.Length == 0) return Array.Empty<string>();
        // TrimEntries + RemoveEmptyEntries collapses the pre-existing
        // trim-then-skip-empties loop into the BCL split options.
        return input.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// Immutable bundle of the five profile-view values. Matches the
/// member shape of <see cref="IProfileConfigSource"/>.
/// </summary>
public sealed record ProfileView(
    IReadOnlyDictionary<string, ProfileDef> ParsedProfiles,
    IReadOnlyList<string> ProfileOrder,
    string? DefaultProfileId,
    IReadOnlySet<string> HiddenProfileIds,
    IReadOnlyList<string> ProfileWarnings);
