using Ghostty.Core.Gaps;
using Xunit;

namespace Ghostty.Tests.Gaps;

public class RenderedIssueTests
{
    [Fact]
    public void Form_rendered_body_matches_its_key()
    {
        var body = RenderedIssue.Parse(GapTestKeys.FormBody(GapTestKeys.Tracked));

        Assert.NotNull(body);
        Assert.True(body!.MatchesKey(GapTestKeys.Tracked));
    }

    [Fact]
    public void Hand_written_gap_line_without_form_sections_does_not_match()
    {
        var k = GapTestKeys.Tracked;
        var body = RenderedIssue.Parse($"Gap: {k.Qualified}\n\n{k.Token}\n");

        Assert.False(body!.MatchesKey(k));
    }

    [Fact]
    public void Prefix_key_body_does_not_match_the_longer_key()
    {
        var body = RenderedIssue.Parse(GapTestKeys.FormBody(GapTestKeys.Prefix));

        Assert.False(body!.MatchesKey(GapTestKeys.PrefixLonger));
    }

    [Fact]
    public void A_second_token_anywhere_disqualifies_the_body()
    {
        var body = RenderedIssue.Parse(GapTestKeys.FormBody(
            GapTestKeys.Tracked, extra: "also " + GapTestKeys.Prefix.Token));

        Assert.False(body!.MatchesKey(GapTestKeys.Tracked));
    }

    [Fact]
    public void Key_field_that_disagrees_with_the_token_does_not_match()
    {
        var k = GapTestKeys.Tracked;
        var text = GapTestKeys.FormBody(k).Replace(k.Qualified, GapTestKeys.PrefixLonger.Qualified);

        Assert.False(RenderedIssue.Parse(text)!.MatchesKey(k));
    }

    [Fact]
    public void No_response_reads_as_empty_and_code_spans_are_unwrapped()
    {
        var withName = RenderedIssue.Parse(GapTestKeys.FormBody(GapTestKeys.UnknownVerb, rawName: "worktree"));
        var without = RenderedIssue.Parse(GapTestKeys.FormBody(GapTestKeys.UnknownVerb));

        Assert.Equal("worktree", withName!.Field(GapForm.RawNameId));
        Assert.Equal(string.Empty, without!.Field(GapForm.RawNameId));
    }

    [Fact]
    public void A_repeated_heading_cannot_override_the_first_value()
    {
        var k = GapTestKeys.Tracked;
        var text = GapTestKeys.FormBody(k) + $"\n\n### Gap key\n\n{GapTestKeys.Prefix.Qualified}\n";

        Assert.Equal(k.Qualified, RenderedIssue.Parse(text)!.Field(GapForm.KeyId));
    }

    [Fact]
    public void Oversized_body_is_not_parsed()
    {
        var text = GapTestKeys.FormBody(GapTestKeys.Tracked) + new string('x', RenderedIssue.MaxBodyChars);

        Assert.Null(RenderedIssue.Parse(text));
    }
}
