using System.Text;
using Ghostty.Core.Gaps;
using Xunit;

namespace Ghostty.Tests.Gaps;

public class GapsFileTests
{
    private static GapsFile Parse(string json) =>
        GapsFile.Parse(Encoding.UTF8.GetBytes(json), GapTestKeys.Lookup);

    private static string Doc(string entries) => "{\"version\":1,\"entries\":[" + entries + "]}";

    [Fact]
    public void Planned_entry_with_issue_is_read()
    {
        var file = Parse(Doc("""{"key":"herdr.pane.placement.zoomed","disposition":"planned","issue":42}"""));

        Assert.True(file.TryGet(GapTestKeys.Tracked, out var e));
        Assert.Equal(GapDisposition.Planned, e.Disposition);
        Assert.Equal(42, e.Issue);
    }

    [Fact]
    public void Unknown_disposition_reads_as_tracked()
    {
        var file = Parse(Doc("""{"key":"herdr.pane.placement.zoomed","disposition":"snoozed","issue":7}"""));

        Assert.True(file.TryGet(GapTestKeys.Tracked, out var e));
        Assert.Equal(GapDisposition.Tracked, e.Disposition);
    }

    [Theory]
    [InlineData("""{"key":"herdr.no.such.key","disposition":"planned","issue":1}""")]
    [InlineData("""{"key":"herdr.pane.placement.zoomed","disposition":"planned","issue":"12; evil"}""")]
    [InlineData("""{"key":"herdr.pane.placement.zoomed","disposition":"planned","issue":-3}""")]
    [InlineData("""{"key":"herdr.pane.placement.zoomed","disposition":"planned"}""")]
    [InlineData("""{"key":"herdr.pane.placement.zoomed","disposition":"fixed","issue":5}""")]
    [InlineData("""{"key":"herdr.pane.placement.zoomed","disposition":"fixed","fixed_in":"<b>","issue":5}""")]
    [InlineData("""{"key":"herdr.pane.placement.zoomed","disposition":"by-design"}""")]
    public void Invalid_or_forbidden_entries_are_ignored(string entry)
    {
        var file = Parse(Doc(entry));

        Assert.False(file.TryGet(GapTestKeys.Tracked, out _));
    }

    [Fact]
    public void Compiled_final_dispositions_cannot_be_overridden()
    {
        var file = Parse(Doc("""{"key":"herdr.host.takeover","disposition":"planned","issue":9}"""));

        Assert.False(file.TryGet(GapTestKeys.ByDesign, out _));
    }

    [Fact]
    public void Unknown_bucket_keys_are_never_bound()
    {
        var file = Parse(Doc("""{"key":"herdr.unknown.verb","disposition":"planned","issue":9}"""));

        Assert.False(file.TryGet(GapTestKeys.UnknownVerb, out _));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"entries\":[]}")]
    [InlineData("{\"version\":1}")]
    public void Malformed_files_are_empty(string json)
    {
        Assert.Same(GapsFile.Empty, Parse(json));
    }
}
