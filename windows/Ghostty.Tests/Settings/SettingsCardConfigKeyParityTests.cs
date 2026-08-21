using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Ghostty.Core.Settings;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
//
// This pins the markup, not the running visual tree: a key on a control
// inside a collapsed panel reads as present here, while the search hit still
// lands on nothing until the page reveals it.
public class SettingsCardConfigKeyParityTests
{
    private const string PagePrefix = "Ghostty.Tests.Settings.Pages.";

    // The shell file that states the Page -> tag -> page-type routing these
    // checks follow. Parsed rather than referenced: it lives in the WinUI
    // project, which this assembly cannot reference.
    private const string ShellWindow = "Settings.SettingsWindow.xaml.cs";

    // The XAML prefix is an alias (ctrl:), but the namespace it resolves to
    // is what identifies the property, so a renamed alias changes nothing.
    private static readonly XNamespace ControlsNamespace = "using:Ghostty.Controls.Settings";
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string AttachedName = "SettingsCard.ConfigKey";

    // Indexed so search can surface them, but not editable from any settings
    // page yet. Search hits for these navigate to the page without scrolling
    // to anything, which is a gap in the UI, not a broken key. Delete an
    // entry here when its control lands. Keep the array (even empty) so the
    // stale-exemption test still compiles.
    private static readonly string[] KeysWithNoControlYet =
    {
    };

    [Fact]
    public void EveryConfigKeyInXamlIsIndexed()
    {
        var indexed = SettingsIndex.All.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        var unindexed = ConfigKeysByPageType()
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
        var pages = ConfigKeysByPageType();
        var pageTypes = PageTypesByIndexName();
        var problems = new List<string>();

        foreach (var entry in SettingsIndex.All)
        {
            if (KeysWithNoControlYet.Contains(entry.Key, StringComparer.Ordinal)) continue;

            // An unrouted page is EveryIndexedPageIsRoutedBySettingsWindow's
            // report to make. Saying it here too turns one defect into two red
            // tests with overlapping text.
            if (!pageTypes.TryGetValue(entry.Page, out var expectedPage)) continue;

            if (!pages.TryGetValue(expectedPage, out var keys))
            {
                problems.Add(
                    $"  {entry.Key}: Page \"{entry.Page}\" resolves to {expectedPage}, " +
                    "which is not in the scanned corpus");
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
        var tagged = ConfigKeysByPageType().Values
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

    // The breadcrumb in the results pane reads "<Page> > <Section>", and the
    // tree walk stops at the first match, so a Section naming a group the card
    // does not live in sends the user looking under the wrong heading.
    [Fact]
    public void EveryIndexedSectionNamesTheGroupItsControlLivesIn()
    {
        var controls = TaggedControls()
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var mismatched = SettingsIndex.All
            .Where(e => controls.TryGetValue(e.Key, out var c) && c.Group != e.Section)
            .Select(e => $"  {e.Key}: indexed under \"{e.Section}\", but its control " +
                         $"sits in \"{controls[e.Key].Group ?? "no SettingsGroup"}\"")
            .ToList();

        if (mismatched.Count > 0)
        {
            Assert.Fail(
                "SettingsIndex sections that name a group their control does not " +
                "live in. Correct the entry's Section, or move the control:\n" +
                string.Join("\n", mismatched));
        }
    }

    // FindByConfigKey returns the first match in visual-tree order, so a key
    // used twice silently picks one control and strands the other.
    [Fact]
    public void NoConfigKeyIsUsedTwice()
    {
        var duplicates = TaggedControls()
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"  {g.Key}: {string.Join(", ", g.Select(c => c.Page))}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        if (duplicates.Count > 0)
        {
            Assert.Fail(
                "The same ConfigKey tags more than one control. Search reaches only " +
                "the first one found:\n" + string.Join("\n", duplicates));
        }
    }

    // An entry naming a page no PageMapping claims is a dead search result:
    // PageTagFor returns null, the navigation is dropped, and choosing the hit
    // does nothing at all. A mapping whose tag ShowPage has no arm for is the
    // same story one hop later, with a null page assigned to the frame.
    //
    // SettingsPagesAreEmbedded skips an entry it cannot resolve, so this is the
    // test that has to notice. It reports per page rather than per key, since
    // one unrouted page strands every setting on it.
    [Fact]
    public void EveryIndexedPageIsRoutedBySettingsWindow()
    {
        var routed = PageTypesByIndexName();

        var unrouted = SettingsIndex.All
            .Select(e => e.Page)
            .Distinct(StringComparer.Ordinal)
            .Where(page => !routed.ContainsKey(page))
            .OrderBy(page => page, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unrouted.Count == 0,
            "SettingsIndex names pages SettingsWindow does not route. Search finds " +
            "the entry and then goes nowhere. Add a PageMapping with this exact " +
            "IndexName and a ShowPage arm for its tag, or correct the entry's " +
            $"Page: {string.Join(", ", unrouted)}");
    }

    // A well-formed routing table proves nothing if nothing reads it, and these
    // three call sites are the whole path from a search hit to a page on screen.
    // Each has a mutation that leaves every other test in this file green:
    // PageTagFor rewritten to `=> null` kills every search result; dropping the
    // ShowPage call strands the sidebar and opens the window on an empty frame;
    // and `ContentFrame.Content ??= page` computes the right page forever while
    // showing whichever one loaded first.
    [Fact]
    public void TheRoutingTableIsStillRead()
    {
        var window = ShellSource.Load(ShellWindow);

        Assert.Contains(
            "_pageMappings", window.Method("PageTagFor").ToString(), StringComparison.Ordinal);

        Assert.NotEmpty(window.Method("NavView_SelectionChanged").Calls("ShowPage"));

        var shown = window.Method("ShowPage").DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "ContentFrame.Content")
            .ToList();

        Assert.True(
            shown.Count == 1 && shown[0].IsKind(SyntaxKind.SimpleAssignmentExpression),
            "ShowPage must assign its page to ContentFrame.Content unconditionally. A "
            + "compound assignment leaves whichever page loaded first on screen while "
            + "every navigation quietly computes the right one.");
    }

    // Guards the csproj wildcard. Losing it, or moving the pages, would make
    // the checks above scan an empty corpus. EveryIndexedKeyHasAControlOnItsOwnPage
    // already fails loudly in that case; this names the cause.
    [Fact]
    public void SettingsPagesAreEmbedded()
    {
        var pages = ConfigKeysByPageType();
        var pageTypes = PageTypesByIndexName();

        // Without this, a routing parse that resolved nothing would leave the
        // filter below with nothing to check and this test would report success
        // while naming no page at all - the one failure it exists to name.
        Assert.NotEmpty(pageTypes);

        var missing = SettingsIndex.All
            .Select(e => pageTypes.GetValueOrDefault(e.Page))
            .Where(type => type is not null && !pages.ContainsKey(type))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Settings pages named by SettingsIndex are not embedded in the test " +
            "assembly. Check the EmbeddedResource wildcard in Ghostty.Tests.csproj: " +
            $"{string.Join(", ", missing)}");
    }

    // The page TYPE each indexed Page name resolves to, read out of
    // SettingsWindow instead of derived from the name.
    //
    // The shell routes a search hit in two hops: PageTagFor looks the entry's
    // Page up in _pageMappings for a tag, and ShowPage's switch turns that tag
    // into a page instance. Following those two hops is what keeps this in step
    // with where the user actually lands.
    //
    // A type rather than a file name because that is what the corpus is keyed
    // by too, so nothing here has to assume the two spellings match. What this
    // replaces concatenated the page name onto "Page.xaml", which agreed with
    // the shell only while every page name was a single word; a name with a
    // space in it asked for a file that could not exist, and reported every key
    // on that page as unreachable while the shell routed it correctly.
    private static Dictionary<string, string> PageTypesByIndexName()
    {
        var window = ShellSource.Load(ShellWindow);

        // ShellSource parses with no preprocessor symbols defined, so a region
        // the compiler SKIPS is one this reads as live, and the region it keeps
        // is invisible. Either way the table read here would not be the table
        // that ships. Refusing the file is cheaper and more honest than
        // guessing at the shell's symbol set.
        Assert.DoesNotContain("#if", window.Root.ToFullString(), StringComparison.Ordinal);

        // Scoped to the assignment rather than swept from the whole file. A
        // file-wide search reads any PageMapping built anywhere, so a stray one
        // above the constructor would shadow the real row while the same stray
        // below it would not, and a guard whose answer depends on declaration
        // order is not a guard. Being scoped also means every construction in
        // here is a row, so a target-typed new(...) needs no special case.
        var table = window.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_pageMappings")
            .ToList();
        Assert.True(
            table.Count == 1,
            $"expected one _pageMappings assignment, found {table.Count}");

        // PageMapping(tag, indexName, navItem). A null indexName marks a page
        // that hosts no indexed settings, so search never routes to it.
        // Grouped rather than keyed directly because PageTagFor stops at its
        // first match, and a table that named one page twice should be read the
        // way the shell reads it rather than throwing here.
        var tagsByIndexName = table[0]
            .DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Where(o => o.ArgumentList is { Arguments.Count: >= 2 })
            .Select(o => (Tag: PositionalLiteral(o, 0), Index: PositionalLiteral(o, 1)))
            .Where(m => m.Tag is not null && m.Index is not null)
            .GroupBy(m => m.Index!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Tag!, StringComparer.Ordinal);

        // ShowPage's switch arms, "tag" => new Pages.SomethingPage(...), taken
        // from the one top-level switch so that a switch nested inside an arm
        // cannot contribute arms of its own and collide.
        var switches = window.Method("ShowPage")
            .DescendantNodes().OfType<SwitchExpressionSyntax>().ToList();
        Assert.True(
            switches.Count == 1,
            $"expected one switch in ShowPage, found {switches.Count}");

        var typesByTag = switches[0].Arms
            .Select(arm => (
                Tag: arm.Pattern is ConstantPatternSyntax constant
                    ? StringLiteral(constant.Expression)
                    : null,
                Type: (arm.Expression as ObjectCreationExpressionSyntax)
                    ?.Type.ToString().Split('.')[^1]))
            .Where(a => a.Tag is not null && a.Type is not null)
            .GroupBy(a => a.Tag!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Type!, StringComparer.Ordinal);

        // A mapping whose tag has no arm builds no page, so it resolves to no
        // type. EveryIndexedPageIsRoutedBySettingsWindow is what reports that,
        // rather than it disappearing quietly here.
        return tagsByIndexName
            .Where(m => typesByTag.ContainsKey(m.Value))
            .ToDictionary(m => m.Key, m => typesByTag[m.Value], StringComparer.Ordinal);
    }

    // The value of a positional string literal argument, or null for anything
    // else. Named arguments are refused rather than read in order, since
    // PageMapping(indexName: ..., tag: ...) compiles and would otherwise be
    // read the wrong way round.
    private static string? PositionalLiteral(
        BaseObjectCreationExpressionSyntax creation, int index)
    {
        var argument = creation.ArgumentList!.Arguments[index];
        return argument.NameColon is null ? StringLiteral(argument.Expression) : null;
    }

    // The value of a string literal, or null for anything else: a null, a
    // named constant, an interpolation. Callers drop those rather than
    // guessing at them.
    private static string? StringLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    // Keyed by the type the markup declares, not by the file it sits in. In
    // WinUI those are paired by x:Class and nothing makes the two read the
    // same, so a page renamed on disk, or a stale copy left beside the live
    // one, would otherwise satisfy every check here while the page that
    // actually loads has been stripped.
    private static Dictionary<string, HashSet<string>> ConfigKeysByPageType()
        => TaggedControls()
            .Where(c => c.Type is not null)
            .GroupBy(c => c.Type!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => c.Key).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static List<(string Page, string? Type, string Key, string? Group)> TaggedControls()
        => PageDocuments()
            .SelectMany(p => p.Document.Descendants()
                .SelectMany(e => e.Attributes()
                    .Where(a => a.Name == ControlsNamespace + AttachedName)
                    .Select(a => (
                        p.Page,
                        Type: PageTypeOf(p.Document),
                        Key: a.Value,
                        Group: EnclosingGroupHeader(e)))))
            .ToList();

    // The tail of the root element's x:Class, which is the type this markup
    // belongs to. Null for markup that declares none, which is not a page.
    private static string? PageTypeOf(XDocument document)
        => document.Root?.Attribute(XamlNamespace + "Class")?.Value.Split('.')[^1];

    // Cards are grouped visually by SettingsGroup, and its Header is what an
    // entry's Section has to name for the search breadcrumb to be followable.
    private static string? EnclosingGroupHeader(XElement element)
        => element.AncestorsAndSelf()
            .FirstOrDefault(a => a.Name == ControlsNamespace + "SettingsGroup")
            ?.Attribute("Header")?.Value;

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

            var page = resource[PagePrefix.Length..];
            try
            {
                pages.Add((page, XDocument.Load(stream)));
            }
            catch (XmlException ex)
            {
                // XDocument.Load has no base URI here, so its message names a
                // line but not a file, and every test in this class reports it.
                Assert.Fail($"{page} is not well-formed XML: {ex.Message}");
            }
        }

        return pages;
    }
}
