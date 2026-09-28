using System.Text;
using Ghostty.Core.Gaps;
using Xunit;

namespace Ghostty.Tests.Gaps;

public class GapLookupTests
{
    private static readonly ReleaseVersion Running = V("1.2.0");

    private static ReleaseVersion V(string s)
    {
        Assert.True(ReleaseVersion.TryParse(s, out var v));
        return v;
    }

    private static GapsFile Gaps(string entry) =>
        GapsFile.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"entries\":[" + entry + "]}"), GapTestKeys.Lookup);

    private static GapSearchHit Hit(int n, GapKey key, bool open = true, string? reason = null,
        bool triaged = false, string? rawName = null) =>
        new(n, open, reason, triaged ? ["gap", "gap:triaged"] : ["gap"], GapTestKeys.FormBody(key, rawName));

    [Fact]
    public void Final_compiled_dispositions_never_offer_report_or_look_up()
    {
        var na = GapLookup.Decide(GapTestKeys.NotApplicable, GapsFile.Empty, Running, null, null);
        var bd = GapLookup.Decide(GapTestKeys.ByDesign, GapsFile.Empty, Running, null, null);

        Assert.Equal(GapRowKind.NotApplicable, na.Kind);
        Assert.False(na.ReportAvailable);
        Assert.Equal(GapRowKind.ByDesign, bd.Kind);
        Assert.False(bd.ReportAvailable);
    }

    [Fact]
    public void Fixed_in_a_newer_release_on_the_users_channel_hides_report()
    {
        var gaps = Gaps("""{"key":"herdr.pane.placement.zoomed","disposition":"fixed","fixed_in":"1.3.0","issue":5}""");

        var row = GapLookup.Decide(GapTestKeys.Tracked, gaps, Running, channelLatest: V("1.3.0"), hits: []);

        Assert.Equal(GapRowKind.FixedInNewer, row.Kind);
        Assert.False(row.ReportAvailable);
    }

    [Theory]
    [InlineData("1.2.5")]
    [InlineData(null)]
    public void Fixed_but_not_on_the_users_channel_keeps_report(string? channel)
    {
        var gaps = Gaps("""{"key":"herdr.pane.placement.zoomed","disposition":"fixed","fixed_in":"1.3.0","issue":5}""");

        var row = GapLookup.Decide(GapTestKeys.Tracked, gaps, Running,
            channel is null ? null : V(channel), hits: []);

        Assert.Equal(GapRowKind.LinkBound, row.Kind);
        Assert.True(row.FixedNotYetReleased);
        Assert.True(row.ReportAvailable);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.1.0")]
    public void At_or_past_fixed_in_and_still_firing_is_a_regression_of_the_bound_issue(string fixedIn)
    {
        var gaps = Gaps($$"""{"key":"herdr.pane.placement.zoomed","disposition":"fixed","fixed_in":"{{fixedIn}}","issue":5}""");
        var closedOriginal = Hit(5, GapTestKeys.Tracked, open: false, reason: "completed", triaged: true);

        var row = GapLookup.Decide(GapTestKeys.Tracked, gaps, Running, V("1.2.0"), [closedOriginal]);

        Assert.Equal(GapRowKind.Regression, row.Kind);
        Assert.True(row.ReportAvailable);
        Assert.Equal(5, row.RegressionOf);
    }

    [Fact]
    public void A_gaps_json_binding_links_the_issue_and_hides_report()
    {
        var gaps = Gaps("""{"key":"herdr.pane.placement.zoomed","disposition":"planned","issue":77}""");

        var row = GapLookup.Decide(GapTestKeys.Tracked, gaps, Running, null, hits: []);

        Assert.Equal(GapRowKind.LinkBound, row.Kind);
        Assert.Equal(77, row.BoundIssue);
        Assert.False(row.ReportAvailable);
    }

    [Fact]
    public void Outsider_issue_closed_as_completed_never_hides_report()
    {
        var hit = Hit(12, GapTestKeys.Tracked, open: false, reason: "completed");

        var row = GapLookup.Decide(GapTestKeys.Tracked, GapsFile.Empty, Running, null, [hit]);

        Assert.Equal(GapRowKind.Report, row.Kind);
        Assert.True(row.ReportAvailable);
        Assert.Equal(1, row.UntriagedCount);
    }

    [Fact]
    public void Unbound_triaged_hit_is_listed_by_number_and_report_stays_available()
    {
        var row = GapLookup.Decide(GapTestKeys.Tracked, GapsFile.Empty, Running, null,
            [Hit(30, GapTestKeys.Tracked, triaged: true), Hit(31, GapTestKeys.Tracked)]);

        Assert.True(row.ReportAvailable);
        Assert.Equal([30], row.TriagedHits);
        Assert.Equal(1, row.UntriagedCount);
    }

    [Fact]
    public void Duplicate_closed_and_non_matching_hits_are_ignored()
    {
        var row = GapLookup.Decide(GapTestKeys.Tracked, GapsFile.Empty, Running, null,
        [
            Hit(40, GapTestKeys.Tracked, open: false, reason: "duplicate", triaged: true),
            Hit(41, GapTestKeys.PrefixLonger, triaged: true),
        ]);

        Assert.Empty(row.TriagedHits!);
        Assert.Equal(0, row.UntriagedCount);
    }

    [Fact]
    public void Unknown_bucket_hits_match_only_on_the_same_raw_name()
    {
        var hits = new[]
        {
            Hit(50, GapTestKeys.UnknownVerb, triaged: true, rawName: "bar"),
            Hit(51, GapTestKeys.UnknownVerb),
        };

        var foo = GapLookup.Decide(GapTestKeys.UnknownVerb, GapsFile.Empty, Running, null, hits, rawName: "foo");
        var bar = GapLookup.Decide(GapTestKeys.UnknownVerb, GapsFile.Empty, Running, null, hits, rawName: "bar");

        Assert.Empty(foo.TriagedHits!);
        Assert.Equal(0, foo.UntriagedCount);
        Assert.Equal([50], bar.TriagedHits);
        Assert.True(bar.ReportAvailable);
    }

    [Fact]
    public void Failed_search_opens_report_and_says_duplicates_were_not_checked()
    {
        var row = GapLookup.Decide(GapTestKeys.Tracked, GapsFile.Empty, Running, null, hits: null);

        Assert.Equal(GapRowKind.Report, row.Kind);
        Assert.True(row.CouldNotCheck);
    }
}
