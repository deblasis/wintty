using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

public class SettingsSearchTests
{
    // Match tiers, best → worst, per Phase 3 spec
    // (docs/superpowers/specs/2026-04-14-settings-reorganization-design.md#matching):
    //   1. ExactLabel        2. LabelPrefix      3. LabelContains
    //   4. DescriptionContains  5. TagExact      6. TagContains
    //   7. FuzzyKey
    //
    // Tests use tiny per-test corpora so each assertion isolates one
    // tier boundary without accidental cross-field hits.

    private static SettingsEntry Make(
        string key,
        string label = "Label",
        string description = "Description.",
        string[]? tags = null) =>
        new(key, label, description, "Page", "Section",
            tags ?? new[] { "misc" }, SettingType.Text);

    [Fact]
    public void Empty_query_returns_no_hits()
    {
        var corpus = new[] { Make("a", label: "Alpha") };
        Assert.Empty(SettingsSearch.Search("", corpus));
        Assert.Empty(SettingsSearch.Search("   ", corpus));
        Assert.Empty(SettingsSearch.Search(null!, corpus));
    }

    [Fact]
    public void Exact_label_beats_label_prefix()
    {
        var exact  = Make("a", label: "Font");
        var prefix = Make("b", label: "Font family");
        var hits = SettingsSearch.Search("Font", new[] { prefix, exact });
        Assert.Equal("a", hits[0].Entry.Key);
        Assert.Equal(MatchKind.ExactLabel, hits[0].Kind);
        Assert.Equal("b", hits[1].Entry.Key);
        Assert.Equal(MatchKind.LabelPrefix, hits[1].Kind);
    }

    [Fact]
    public void Label_prefix_beats_label_contains()
    {
        var prefix   = Make("a", label: "Cursor style");
        var contains = Make("b", label: "Hide cursor while typing");
        var hits = SettingsSearch.Search("cursor", new[] { contains, prefix });
        Assert.Equal("a", hits[0].Entry.Key);
        Assert.Equal(MatchKind.LabelPrefix, hits[0].Kind);
        Assert.Equal("b", hits[1].Entry.Key);
        Assert.Equal(MatchKind.LabelContains, hits[1].Kind);
    }

    [Fact]
    public void Label_contains_beats_description_contains()
    {
        var label = Make("a", label: "Bar foo baz");
        var desc  = Make("b", label: "Unrelated", description: "Contains foo inside.");
        var hits = SettingsSearch.Search("foo", new[] { desc, label });
        Assert.Equal("a", hits[0].Entry.Key);
        Assert.Equal(MatchKind.LabelContains, hits[0].Kind);
        Assert.Equal("b", hits[1].Entry.Key);
        Assert.Equal(MatchKind.DescriptionContains, hits[1].Kind);
    }

    [Fact]
    public void Description_contains_beats_tag_exact()
    {
        var desc = Make("a", label: "Unrelated", description: "foo appears here.");
        var tag  = Make("b", label: "Other", description: "Nothing.", tags: new[] { "foo" });
        var hits = SettingsSearch.Search("foo", new[] { tag, desc });
        Assert.Equal("a", hits[0].Entry.Key);
        Assert.Equal(MatchKind.DescriptionContains, hits[0].Kind);
        Assert.Equal("b", hits[1].Entry.Key);
        Assert.Equal(MatchKind.TagExact, hits[1].Kind);
    }

    [Fact]
    public void Tag_exact_beats_tag_contains()
    {
        var exact    = Make("a", tags: new[] { "foo" });
        var contains = Make("b", tags: new[] { "foobar" });
        var hits = SettingsSearch.Search("foo", new[] { contains, exact });
        Assert.Equal("a", hits[0].Entry.Key);
        Assert.Equal(MatchKind.TagExact, hits[0].Kind);
        Assert.Equal("b", hits[1].Entry.Key);
        Assert.Equal(MatchKind.TagContains, hits[1].Kind);
    }

    [Fact]
    public void Fuzzy_key_match_is_last_resort()
    {
        // "bgop" isn't a substring of any label, description, or tag,
        // but the characters appear in order in "background-opacity".
        var entry = Make("background-opacity", label: "Opac", description: "x", tags: new[] { "xxx" });
        var other = Make("scrollback-limit", label: "Scroll", description: "x", tags: new[] { "yyy" });
        var hits = SettingsSearch.Search("bgop", new[] { other, entry });
        Assert.Single(hits);
        Assert.Equal("background-opacity", hits[0].Entry.Key);
        Assert.Equal(MatchKind.FuzzyKey, hits[0].Kind);
    }

    [Fact]
    public void Fuzzy_key_requires_characters_in_order()
    {
        // "ob" is not a subsequence of "abc" (no 'o' before 'b').
        var entry = Make("abc");
        Assert.Empty(SettingsSearch.Search("ob", new[] { entry }));
    }

    [Fact]
    public void No_match_returns_empty()
    {
        var corpus = new[] { Make("a", label: "Alpha") };
        Assert.Empty(SettingsSearch.Search("zzzqqq", corpus));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var corpus = new[]
        {
            Make("a", label: "Background"),
            Make("b", label: "Other", tags: new[] { "BACKGROUND" }),
        };
        var lower = SettingsSearch.Search("background", corpus).Select(h => h.Entry.Key);
        var upper = SettingsSearch.Search("BACKGROUND", corpus).Select(h => h.Entry.Key);
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Results_are_ordered_by_match_tier_descending()
    {
        var entries = new[]
        {
            Make("a", label: "Background"),                                     // ExactLabel
            Make("b", label: "Background opacity"),                             // LabelPrefix
            Make("c", label: "Colors", description: "background tint layer."),  // DescriptionContains
            Make("d", label: "Other", tags: new[] { "background" }),            // TagExact
        };
        var hits = SettingsSearch.Search("background", entries);
        var tiers = hits.Select(h => (int)h.Kind).ToList();
        for (int i = 1; i < tiers.Count; i++)
        {
            Assert.True(
                tiers[i - 1] >= tiers[i],
                $"tier dropped then rose: {string.Join(", ", tiers)}");
        }
    }

    [Fact]
    public void Each_entry_appears_at_most_once_at_best_tier()
    {
        // "foo" matches tag "foo" (exact) AND description ("foo sits...").
        // Description is a higher tier; only one hit, at that tier.
        var e = Make("a", label: "Other", description: "foo sits here.", tags: new[] { "foo" });
        var hits = SettingsSearch.Search("foo", new[] { e });
        Assert.Single(hits);
        Assert.Equal(MatchKind.DescriptionContains, hits[0].Kind);
    }

    [Fact]
    public void Ties_within_tier_preserve_input_order()
    {
        var first  = Make("first",  label: "Background A");
        var second = Make("second", label: "Background B");
        var hits = SettingsSearch.Search("background", new[] { first, second });
        Assert.Equal("first",  hits[0].Entry.Key);
        Assert.Equal("second", hits[1].Entry.Key);
    }
}
