using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

// Pins the SettingsCard.ConfigKey attached properties in the settings page
// XAML against SettingsIndex.
//
// Search results are routed by key in two legs: SettingsWindow navigates to
// the entry's Page, and only then does SettingsCardLocator.FindByConfigKey
// walk THAT page's visual tree for the key. A key that exists on only one
// side of the handshake, or that sits on a different page than the index
// claims, fails silently -- the page opens, nothing scrolls, nothing pulses,
// no error anywhere. A typo'd ConfigKey (background-gradient-blend-mode for
// background-gradient-blend) shipped exactly that way.
//
// The pages are parsed as XML rather than text-scanned so that a key inside
// an XML comment does not count as a live one. The XAML is embedded by
// Ghostty.Tests.csproj rather than read from disk, so the test does not
// depend on where the assembly runs from, and does not need a project
// reference to Ghostty.csproj.
public class SettingsCardConfigKeyParityTests
{
    private const string PagePrefix = "Ghostty.Tests.Settings.Pages.";

    // The XAML prefix is an alias (ctrl:), but the namespace it resolves to
    // is what identifies the property, so a renamed alias changes nothing.
    private static readonly XNamespace ControlsNamespace = "using:Ghostty.Controls.Settings";
    private const string AttachedName = "SettingsCard.ConfigKey";

    // Indexed so search can surface them, but not editable from any settings
    // page yet. Search hits for these navigate to the page without scrolling
    // to anything, which is a gap in the UI, not a broken key. Delete an
    // entry here when its control lands.
    private static readonly string[] KeysWithNoControlYet =
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

    // Checks the key AND the page it lives on. Asserting only that the key
    // exists somewhere would let an entry point search at the wrong page,
    // where the tree walk cannot see the control.
    [Fact]
    public void EveryIndexedKeyHasAControlOnItsOwnPage()
    {
        var pages = ConfigKeysByPage();
        var problems = new List<string>();

        foreach (var entry in SettingsIndex.All)
        {
            if (KeysWithNoControlYet.Contains(entry.Key, StringComparer.Ordinal)) continue;

            var expectedPage = PageFileFor(entry.Page);
            if (!pages.TryGetValue(expectedPage, out var keys))
            {
                problems.Add(
                    $"  {entry.Key}: Page \"{entry.Page}\" has no {expectedPage} in the " +
                    "scanned corpus");
                continue;
            }

            if (keys.Contains(entry.Key)) continue;

            var elsewhere = pages.Where(p => p.Value.Contains(entry.Key))
                .Select(p => p.Key).ToList();
            problems.Add(elsewhere.Count > 0
                ? $"  {entry.Key}: indexed under \"{entry.Page}\" but tagged in " +
                  string.Join(", ", elsewhere)
                : $"  {entry.Key}: no ConfigKey anywhere in {expectedPage}");
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "SettingsIndex entries whose control search cannot reach. Choosing one " +
                "of these in search navigates to the page but never scrolls to or " +
                "pulses anything. Add the attached property to the control on that " +
                "page, correct the entry's Page, or list the key in " +
                $"{nameof(KeysWithNoControlYet)} if the setting has no control:\n" +
                string.Join("\n", problems));
        }
    }

    // The scan only recognizes the qualified attribute form. Without this, a
    // ConfigKey written any other way would be absent from the scanned set,
    // and the checks above would report a real key as missing -- or, if the
    // index has no entry for it either, not report it at all.
    [Fact]
    public void ConfigKeyIsAlwaysWrittenAsTheAttachedProperty()
    {
        var problems = new List<string>();

        foreach (var (page, document) in PageDocuments())
        {
            foreach (var element in document.Descendants())
            {
                // Property-element syntax: <ctrl:SettingsCard.ConfigKey>key</...>.
                if (element.Name.LocalName == AttachedName)
                    problems.Add($"  {page}: {AttachedName} set as a property element");

                foreach (var attribute in element.Attributes())
                {
                    var local = attribute.Name.LocalName;

                    if (local == "ConfigKey")
                    {
                        problems.Add(
                            $"  {page}: unqualified ConfigKey on <{element.Name.LocalName}>; " +
                            $"write it as the attached property, {AttachedName}");
                        continue;
                    }

                    if (local != AttachedName) continue;

                    if (attribute.Name.Namespace != ControlsNamespace)
                    {
                        problems.Add(
                            $"  {page}: {AttachedName} resolves to " +
                            $"\"{attribute.Name.Namespace}\", not \"{ControlsNamespace}\"");
                    }
                    else if (string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        problems.Add(
                            $"  {page}: empty {AttachedName} on <{element.Name.LocalName}>");
                    }
                }
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "ConfigKey written in a form the parity checks above cannot see:\n" +
                string.Join("\n", problems));
        }
    }

    // KeysWithNoControlYet is only honest while both halves hold: the keys
    // still exist in the index, and they still have no control. Otherwise a
    // stale entry permanently exempts a real key from the check above.
    [Fact]
    public void KeysWithNoControlYetIsNotStale()
    {
        var tagged = ConfigKeysByPage().Values
            .SelectMany(keys => keys)
            .ToHashSet(StringComparer.Ordinal);
        var indexed = SettingsIndex.All.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        var landed = KeysWithNoControlYet.Where(tagged.Contains).ToList();
        var dropped = KeysWithNoControlYet.Where(k => !indexed.Contains(k)).ToList();

        Assert.True(
            landed.Count == 0,
            "These keys now have a tagged control, so remove them from " +
            $"{nameof(KeysWithNoControlYet)}: {string.Join(", ", landed)}");
        Assert.True(
            dropped.Count == 0,
            "These keys are no longer in SettingsIndex, so remove them from " +
            $"{nameof(KeysWithNoControlYet)}: {string.Join(", ", dropped)}");
    }

    // Guards the csproj wildcard. Losing it, or moving the pages, would make
    // the checks above scan an empty corpus. EveryIndexedKeyHasAControlOnItsOwnPage
    // already fails loudly in that case; this names the cause.
    [Fact]
    public void SettingsPagesAreEmbedded()
    {
        var pages = ConfigKeysByPage();

        var missing = SettingsIndex.All
            .Select(e => PageFileFor(e.Page))
            .Distinct(StringComparer.Ordinal)
            .Where(f => !pages.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Settings pages named by SettingsIndex are not embedded in the test " +
            "assembly. Check the EmbeddedResource wildcard in Ghostty.Tests.csproj: " +
            $"{string.Join(", ", missing)}");
    }

    // SettingsWindow maps an entry's Page to a page instance by its own table;
    // the file names follow the same "<Page>Page.xaml" convention throughout.
    private static string PageFileFor(string page) => $"{page}Page.xaml";

    private static Dictionary<string, HashSet<string>> ConfigKeysByPage()
        => PageDocuments().ToDictionary(
            p => p.Page,
            p => p.Document.Descendants()
                .SelectMany(e => e.Attributes())
                .Where(a => a.Name == ControlsNamespace + AttachedName)
                .Select(a => a.Value)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static List<(string Page, XDocument Document)> PageDocuments()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var pages = new List<(string, XDocument)>();

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(PagePrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            Assert.NotNull(stream);

            pages.Add((resource[PagePrefix.Length..], XDocument.Load(stream)));
        }

        return pages;
    }
}
