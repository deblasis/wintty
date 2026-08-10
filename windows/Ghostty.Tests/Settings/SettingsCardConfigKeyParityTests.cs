using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

// Pins the SettingsCard.ConfigKey attached properties in the settings page
// XAML against SettingsIndex.
//
// Search results are routed by key: SettingsWindow navigates to the entry's
// page and then asks SettingsCardLocator.FindByConfigKey for the matching
// card. A key that exists on only one side of that handshake fails silently
// -- the page opens, nothing scrolls, nothing pulses, no error anywhere. A
// typo'd ConfigKey (background-gradient-blend-mode for
// background-gradient-blend) shipped exactly that way.
//
// The XAML is embedded by Ghostty.Tests.csproj rather than read from disk, so
// the test does not depend on where the assembly runs from, and does not need
// a project reference to Ghostty.csproj.
public class SettingsCardConfigKeyParityTests
{
    private const string PagePrefix = "Ghostty.Tests.Settings.Pages.";

    // ctrl: is the only prefix the pages bind to Ghostty.Controls.Settings,
    // but match any prefix so a renamed namespace alias shows up as a parity
    // failure rather than as an unscanned card. The prefix is optional: on a
    // SettingsCard element itself the attached property can be set unqualified.
    private static readonly Regex ConfigKeyPattern = new(
        @"(?:(?:[A-Za-z_][\w.]*:)?SettingsCard\.)?ConfigKey\s*=\s*""(?<key>[^""]*)""",
        RegexOptions.Compiled);

    // Indexed so search can surface them, but not editable from a settings
    // card yet. Search hits for these navigate to the page without scrolling
    // to anything, which is a gap in the UI, not a broken key. Delete an
    // entry here when its card lands.
    private static readonly string[] KeysWithNoCardYet =
    {
        "command-palette-background",
        "command-palette-group-commands",
    };

    [Fact]
    public void EveryConfigKeyInXamlIsIndexed()
    {
        var indexed = SettingsIndex.All.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        var unindexed = ConfigKeysByPage()
            .SelectMany(page => page.Value.Select(key => (Page: page.Key, Key: key)))
            .Where(c => !indexed.Contains(c.Key))
            .OrderBy(c => c.Page, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        if (unindexed.Count > 0)
        {
            Assert.Fail(
                "SettingsCard.ConfigKey values with no SettingsIndex entry. Either " +
                "the key is a typo (compare against the key ConfigService reads and " +
                "the page's OnValueChanged writes), or the setting needs an entry in " +
                "SettingsIndex.All so search can find it:\n" +
                string.Join("\n", unindexed.Select(c => $"  {c.Key}  ({c.Page})")));
        }
    }

    [Fact]
    public void EveryIndexedKeyHasACard()
    {
        var inXaml = ConfigKeysByPage().Values
            .SelectMany(keys => keys)
            .ToHashSet(StringComparer.Ordinal);

        var cardless = SettingsIndex.All
            .Select(e => e.Key)
            .Where(k => !inXaml.Contains(k))
            .Except(KeysWithNoCardYet, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (cardless.Count > 0)
        {
            Assert.Fail(
                "SettingsIndex entries with no SettingsCard.ConfigKey in any settings " +
                "page. Choosing one of these in search navigates to the page but never " +
                "scrolls to or pulses the card. Add the attached property to the card, " +
                "or list the key in KeysWithNoCardYet if the setting has no card:\n" +
                string.Join("\n", cardless.Select(k => $"  {k}")));
        }
    }

    // KeysWithNoCardYet is only honest while it stays a list of keys that
    // genuinely have no card. Without this, a card added later would leave a
    // stale entry that permanently exempts a real key from the check above.
    [Fact]
    public void KeysWithNoCardYetIsNotStale()
    {
        var inXaml = ConfigKeysByPage().Values
            .SelectMany(keys => keys)
            .ToHashSet(StringComparer.Ordinal);

        var landed = KeysWithNoCardYet.Where(inXaml.Contains).ToList();

        Assert.True(
            landed.Count == 0,
            "These keys now have a SettingsCard, so remove them from " +
            $"KeysWithNoCardYet: {string.Join(", ", landed)}");
    }

    // Without this, a ConfigKey written in a spelling the regex does not match
    // would be absent from the scanned set, and the checks above would report
    // it as a setting with no card -- or, if the index has no entry for it
    // either, not report it at all.
    [Fact]
    public void EveryConfigKeyOccurrenceIsMatched()
    {
        foreach (var (page, source) in PageSources())
        {
            var occurrences = Regex.Matches(source, @"\bConfigKey\b").Count;
            var matched = ConfigKeyPattern.Matches(source).Count;

            Assert.True(
                occurrences == matched,
                $"{page} mentions ConfigKey {occurrences} time(s) but the scan " +
                $"pattern in {nameof(SettingsCardConfigKeyParityTests)} matched " +
                $"{matched}. The pattern no longer describes how the pages set " +
                "the attached property, so the parity checks cannot see every card.");
        }
    }

    // Guards the csproj wildcard. Losing it, or moving the pages, would make
    // every check above scan an empty corpus and pass. Pages that hold no
    // cards at all (keybindings, profiles, the raw editor) legitimately
    // contribute no keys, so this pins the page with the most of them.
    [Fact]
    public void SettingsPagesAreEmbedded()
    {
        var pages = ConfigKeysByPage();

        Assert.NotEmpty(pages);
        Assert.NotEmpty(pages["AppearancePage.xaml"]);
    }

    private static Dictionary<string, HashSet<string>> ConfigKeysByPage()
        => PageSources().ToDictionary(
            p => p.Page,
            p => ConfigKeyPattern.Matches(p.Source)
                .Select(m => m.Groups["key"].Value)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static List<(string Page, string Source)> PageSources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var pages = new List<(string, string)>();

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(PagePrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);

            pages.Add((resource[PagePrefix.Length..], reader.ReadToEnd()));
        }

        return pages;
    }
}
