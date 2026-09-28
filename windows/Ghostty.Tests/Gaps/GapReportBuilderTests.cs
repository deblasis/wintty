using Ghostty.Core.Gaps;
using Xunit;

namespace Ghostty.Tests.Gaps;

public class GapReportBuilderTests
{
    private static GapReportRequest Req(GapKey? key = null) =>
        new(key ?? GapTestKeys.Tracked, WinttyVersion: "1.0.0", Tier: "pro", EmulatedHerdrVersion: "0.9.1");

    private static Dictionary<string, string> Query(string url)
    {
        var q = new Uri(url).Query.TrimStart('?');
        return q.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    [Fact]
    public void Url_targets_the_domain_form_and_passes_only_template_title_and_fields()
    {
        var report = GapReportBuilder.Build(Req());
        var q = Query(report.Url!);

        Assert.StartsWith("https://github.com/deblasis/wintty/issues/new?", report.Url);
        Assert.Equal("gap-herdr.yml", q["template"]);
        Assert.Equal("[gap] herdr.pane.placement.zoomed", q["title"]);
        Assert.Equal(GapTestKeys.Tracked.Token, q[GapForm.TokenId]);
        Assert.DoesNotContain("labels", q.Keys);
        Assert.DoesNotContain("assignees", q.Keys);
        Assert.DoesNotContain("milestone", q.Keys);
        var allowed = new[] { "template", "title", GapForm.TokenId, GapForm.KeyId, GapForm.RawNameId,
            GapForm.PluginNameId, GapForm.WinttyVersionId, GapForm.HerdrVersionId, GapForm.RegressionOfId,
            GapForm.DetailsId };
        Assert.All(q.Keys, k => Assert.Contains(k, allowed));
    }

    [Fact]
    public void Copy_text_parses_back_like_a_submitted_form()
    {
        var report = GapReportBuilder.Build(Req() with { RawName = "worktree", IncludeRawName = true });

        var parsed = RenderedIssue.Parse(report.CopyText);

        Assert.True(parsed!.MatchesKey(GapTestKeys.Tracked));
        Assert.Equal("worktree", parsed.Field(GapForm.RawNameId));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\plugins")]
    [InlineData("@maintainer")]
    [InlineData("#123")]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("line\nbreak")]
    [InlineData("&labels=gap:triaged")]
    [InlineData("wgap-0123456789ab")]
    public void Hostile_names_never_reach_the_report(string hostile)
    {
        var report = GapReportBuilder.Build(Req() with
        {
            RawName = hostile, IncludeRawName = true,
            PluginName = hostile, IncludePluginName = true,
            RequestedMinHerdrVersion = hostile,
        });

        Assert.DoesNotContain(hostile, report.CopyText);
        Assert.DoesNotContain(Uri.EscapeDataString(hostile), report.Url!);
    }

    [Fact]
    public void Link_shaped_names_render_only_inside_code_spans()
    {
        var report = GapReportBuilder.Build(Req() with
        {
            RawName = "www.evil.example.com", IncludeRawName = true,
            PluginName = "www.evil.example.com", IncludePluginName = true,
        });

        Assert.DoesNotContain(" www.evil.example.com", report.CopyText);
        Assert.Contains("`www.evil.example.com`", report.CopyText);
    }

    [Fact]
    public void Names_are_left_out_unless_the_user_opts_in()
    {
        var report = GapReportBuilder.Build(Req() with { RawName = "worktree", PluginName = "zoetrope" });

        Assert.DoesNotContain("worktree", report.CopyText);
        Assert.DoesNotContain("zoetrope", report.CopyText);
    }

    [Fact]
    public void Requested_herdr_version_is_reduced_to_major_minor_patch()
    {
        var report = GapReportBuilder.Build(Req() with { RequestedMinHerdrVersion = "1.2.3-rc.1+alice" });

        Assert.Contains("`1.2.3`", report.CopyText);
        Assert.DoesNotContain("alice", report.CopyText);
    }

    [Fact]
    public void Details_are_dropped_before_the_cap_but_never_the_token_key_or_regression()
    {
        var report = GapReportBuilder.Build(Req() with
        {
            RegressionOf = 5,
            // Non-ASCII percent-encodes to six characters each, so capped details can still overflow the URL.
            Details = new string('\u00E9', 3000),
        });
        var q = Query(report.Url!);

        Assert.True(report.Url!.Length <= GapReportBuilder.MaxUrlLength);
        Assert.DoesNotContain(GapForm.DetailsId, q.Keys);
        Assert.Equal("#5", q[GapForm.RegressionOfId]);
        Assert.Equal(GapTestKeys.Tracked.Qualified, q[GapForm.KeyId]);
        Assert.Contains("### Details", report.Overflow);
    }

    [Fact]
    public void Bidi_and_control_characters_are_stripped_from_details()
    {
        var report = GapReportBuilder.Build(Req() with { Details = "ok\u202Etxt.exe" });

        Assert.DoesNotContain('\u202E', report.CopyText);
    }

    [Fact]
    public void Internal_domain_has_no_form_url()
    {
        var report = GapReportBuilder.Build(Req(GapCatalogue.WinttyVersionMinNewer));

        Assert.Null(report.Url);
    }
}
