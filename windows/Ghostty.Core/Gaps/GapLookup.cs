namespace Ghostty.Core.Gaps;

/// <summary>One issue returned by the GitHub search, as the client sees it.</summary>
public sealed record GapSearchHit(int Number, bool IsOpen, string? StateReason, IReadOnlyList<string> Labels, string? Body);

/// <summary>What a gap's row offers the user.</summary>
public enum GapRowKind
{
    /// <summary>Irrelevant on Windows. No Report, no lookup.</summary>
    NotApplicable,

    /// <summary>Wintty's intended behaviour. No Report, no lookup.</summary>
    ByDesign,

    /// <summary>Already fixed in a release the user's update channel offers. No Report.</summary>
    FixedInNewer,

    /// <summary>Accepted, or fixed but not yet on the user's channel: link the bound issue.</summary>
    LinkBound,

    /// <summary>Fixed in this or an older version and still firing: offer the regression form.</summary>
    Regression,

    /// <summary>Offer Report (possibly alongside unbound hits).</summary>
    Report,
}

/// <summary>The decision for one gap row.</summary>
public sealed record GapRow(
    GapRowKind Kind,
    bool ReportAvailable,
    int? BoundIssue = null,
    ReleaseVersion? FixedIn = null,
    bool FixedNotYetReleased = false,
    int? RegressionOf = null,
    IReadOnlyList<int>? TriagedHits = null,
    int UntriagedCount = 0,
    bool CouldNotCheck = false);

/// <summary>
/// Decides what a gap row offers, from the compiled catalogue, <c>gaps.json</c>,
/// the running version, the update channel and the (untrusted) search results.
/// </summary>
/// <remarks>
/// <para>
/// Only the compiled catalogue and a <c>gaps.json</c> binding can hide Report.
/// Search results never set a disposition: anyone can file an issue carrying a
/// token, apply <c>gap</c> through the form, and close it with any reason.
/// </para>
/// <para>
/// Decision order: final compiled dispositions; <c>fixed</c> (newer and
/// released, not yet released, or a regression); a bound or planned issue;
/// otherwise the matching search hits, shown as triaged numbers or an
/// untriaged count beside Report.
/// </para>
/// </remarks>
public static class GapLookup
{
    /// <param name="key">The gap being shown.</param>
    /// <param name="gaps">Maintainer decisions; <see cref="GapsFile.Empty"/> when unavailable.</param>
    /// <param name="running">The plain release version of this build.</param>
    /// <param name="channelLatest">The newest version offered on the user's update channel, or null when unknown.</param>
    /// <param name="hits">Search results, or null when the search failed (offline, 403, 429, malformed).</param>
    /// <param name="rawName">For <c>herdr.unknown.*</c> keys, the raw name the row stands for; hits match only on it.</param>
    public static GapRow Decide(
        GapKey key,
        GapsFile gaps,
        ReleaseVersion running,
        ReleaseVersion? channelLatest,
        IReadOnlyList<GapSearchHit>? hits,
        string? rawName = null)
    {
        switch (key.Disposition)
        {
            case GapDisposition.NotApplicable: return new GapRow(GapRowKind.NotApplicable, false);
            case GapDisposition.ByDesign: return new GapRow(GapRowKind.ByDesign, false);
        }

        var disposition = key.Disposition;
        int? bound = null;
        ReleaseVersion? fixedIn = null;
        if (gaps.TryGet(key, out var entry))
        {
            disposition = entry.Disposition;
            bound = entry.Issue;
            fixedIn = entry.FixedIn;
        }

        if (disposition == GapDisposition.Fixed && fixedIn is not null)
        {
            if (running.CompareTo(fixedIn) < 0)
            {
                // Fixed in a newer release. Only claim it when the user's channel offers it.
                if (channelLatest is not null && channelLatest.CompareTo(fixedIn) >= 0)
                    return new GapRow(GapRowKind.FixedInNewer, false, bound, fixedIn);
                return new GapRow(GapRowKind.LinkBound, true, bound, fixedIn, FixedNotYetReleased: true);
            }

            // At or past fixed-in and still firing: a regression. The closed
            // original never suppresses the report; hits are shown as usual.
            var (reg, triagedR, untriagedR) = Classify(key, hits, rawName);
            return new GapRow(GapRowKind.Regression, true, bound, fixedIn,
                RegressionOf: bound, TriagedHits: triagedR, UntriagedCount: untriagedR,
                CouldNotCheck: hits is null);
        }

        if (bound is not null)
            return new GapRow(GapRowKind.LinkBound, false, bound);

        var (_, triaged, untriaged) = Classify(key, hits, rawName);
        return new GapRow(GapRowKind.Report, true, TriagedHits: triaged, UntriagedCount: untriaged,
            CouldNotCheck: hits is null);
    }

    private static (bool Any, IReadOnlyList<int> Triaged, int Untriaged) Classify(
        GapKey key, IReadOnlyList<GapSearchHit>? hits, string? rawName)
    {
        if (hits is null) return (false, [], 0);

        var qualifying = new List<GapSearchHit>();
        foreach (var hit in hits)
        {
            if (hit.StateReason == "duplicate") continue;
            var body = RenderedIssue.Parse(hit.Body);
            if (body is null || !body.MatchesKey(key)) continue;
            if (key.IsUnknownBucket)
            {
                var hitName = body.Field(GapForm.RawNameId);
                if (string.IsNullOrEmpty(rawName) || hitName.Length == 0
                    || !string.Equals(hitName, rawName, StringComparison.Ordinal))
                    continue;
            }
            qualifying.Add(hit);
        }

        // Oldest open hit first, then the rest; numbers only, never titles.
        var ordered = qualifying
            .OrderByDescending(h => h.IsOpen)
            .ThenBy(h => h.Number)
            .ToList();
        var triaged = ordered
            .Where(h => h.Labels.Contains(GapForm.TriagedLabel, StringComparer.Ordinal))
            .Select(h => h.Number)
            .ToList();
        return (qualifying.Count > 0, triaged, qualifying.Count - triaged.Count);
    }
}
