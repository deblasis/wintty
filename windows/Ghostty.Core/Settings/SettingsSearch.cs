using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Settings;

/// <summary>
/// Which tier of match an entry produced. Higher enum value == better
/// match; results sort by this descending. None is used internally and
/// never surfaces in <see cref="SearchHit"/>.
///
/// Tier order is set by the Phase 3 spec; see
/// docs/superpowers/specs/2026-04-14-settings-reorganization-design.md.
/// </summary>
public enum MatchKind
{
    None = 0,
    FuzzyKey = 1,
    TagContains = 2,
    TagExact = 3,
    DescriptionContains = 4,
    LabelContains = 5,
    LabelPrefix = 6,
    ExactLabel = 7,
}

public sealed record SearchHit(SettingsEntry Entry, MatchKind Kind);

/// <summary>
/// Tiered substring + fuzzy search over <see cref="SettingsIndex"/>.
/// Lives in Ghostty.Core so the ranking is unit-tested without pulling
/// WinUI 3 into the test assembly.
/// </summary>
public static class SettingsSearch
{
    public static IReadOnlyList<SearchHit> Search(
        string? query,
        IEnumerable<SettingsEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();
        // Only trim; comparisons below use OrdinalIgnoreCase so we don't
        // need to lowercase the query or the entry fields on every hit.
        var q = query.Trim();

        var hits = new List<(int InputIndex, SearchHit Hit)>();
        int i = 0;
        foreach (var entry in entries)
        {
            var kind = Classify(entry, q);
            if (kind != MatchKind.None)
                hits.Add((i, new SearchHit(entry, kind)));
            i++;
        }

        // Stable sort: tier desc, then original input order within a tier
        // so UI grouping (by page/section) stays predictable.
        return hits
            .OrderByDescending(x => (int)x.Hit.Kind)
            .ThenBy(x => x.InputIndex)
            .Select(x => x.Hit)
            .ToList();
    }

    private static MatchKind Classify(SettingsEntry e, string q)
    {
        if (e.Label.Equals(q, StringComparison.OrdinalIgnoreCase)) return MatchKind.ExactLabel;
        if (e.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return MatchKind.LabelPrefix;
        if (e.Label.Contains(q, StringComparison.OrdinalIgnoreCase)) return MatchKind.LabelContains;

        if (e.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            return MatchKind.DescriptionContains;

        var tagExact = false;
        var tagContains = false;
        foreach (var tag in e.Tags)
        {
            if (tag.Equals(q, StringComparison.OrdinalIgnoreCase)) { tagExact = true; break; }
            if (tag.Contains(q, StringComparison.OrdinalIgnoreCase)) tagContains = true;
        }
        if (tagExact) return MatchKind.TagExact;
        if (tagContains) return MatchKind.TagContains;

        if (IsSubsequenceIgnoreCase(q, e.Key)) return MatchKind.FuzzyKey;

        return MatchKind.None;
    }

    // Characters of needle appear in haystack in order (not necessarily
    // contiguous). Classic fuzzy-finder primitive, kept under the spec's
    // "under 50 lines" bar. Case-insensitive via char.ToLowerInvariant so
    // callers don't have to pre-lowercase the inputs.
    private static bool IsSubsequenceIgnoreCase(string needle, string haystack)
    {
        if (needle.Length == 0) return true;
        int ni = 0;
        foreach (var c in haystack)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(needle[ni]))
            {
                ni++;
                if (ni == needle.Length) return true;
            }
        }
        return false;
    }
}
