using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Settings;

// A settings control that writes to the config file on LostFocus has to be
// seeded from the config file first, AND has to write only when the value
// actually changed.
//
// Blur is not an edit. It fires on every pass through the page: tabbing
// through, clicking elsewhere, closing the window. Both halves matter and both
// have shipped broken:
//
//   - Unseeded. AppearancePage's custom-shader box was the one page control
//     with no seed, so opening Appearance and moving focus past it wrote
//     `custom-shader = ` and dropped a configured shader.
//   - Unconditional. AdvancedPage's quake-key box wrote on every blur, and
//     SetValue APPENDS a key the file does not have, so simply visiting
//     Advanced materialised `quick-terminal-key = ` the user never set.
//
// Nothing catches either at runtime: the write is a legitimate config edit, the
// file stays valid, and the setting just quietly becomes something else.
//
// The markup is parsed as XML, so a handler named inside an XML comment does
// not count, and the code-behind is parsed with Roslyn, so the control name
// appearing in a comment does not count as a seed.
//
// What this does NOT see: a LostFocus handler attached in code-behind rather
// than in markup, and a seed written through a property other than Text.
public class SettingsWriteBackSeedingTests
{
    private const string PagePrefix = "Ghostty.Tests.Settings.Pages.";

    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryLostFocusWriteBackControlIsSeededAndGuarded()
    {
        var controls = WriteBackControls();

        // If the scan finds nothing, every assertion below holds vacuously.
        // There is at least one such control in the tree and there has been
        // since the quake key box.
        Assert.True(
            controls.Count > 0,
            "found no LostFocus write-back controls in the settings pages; the " +
            "scan is broken, not the pages");

        var problems = new List<string>();
        foreach (var control in controls)
        {
            var source = SourceFor(control.Page);

            if (!AssignedControlProperties(source).Contains(control.Name + ".Text"))
            {
                problems.Add(
                    $"  {control.Page}: {control.Name} writes the config on " +
                    $"{control.Handler} but nothing assigns {control.Name}.Text, so " +
                    "the first blur writes whatever it is showing, which is nothing");
            }

            if (!ComparesSomething(source, control.Handler))
            {
                problems.Add(
                    $"  {control.Page}: {control.Handler} writes without comparing " +
                    "the value to anything, so every pass through the page rewrites " +
                    "the key -- and appends it if the file does not have it");
            }
        }

        Assert.True(
            problems.Count == 0,
            "settings controls that write on blur without being seeded first, or " +
            "without checking whether anything changed:\n" + string.Join("\n", problems));
    }

    private sealed record WriteBackControl(string Page, string Name, string Handler);

    private static List<WriteBackControl> WriteBackControls()
    {
        var found = new List<WriteBackControl>();
        foreach (var (page, document) in PageDocuments())
        {
            foreach (var element in document.Descendants())
            {
                var handler = element.Attribute("LostFocus")?.Value;
                if (handler is null) continue;

                var name = element.Attribute(XamlNamespace + "Name")?.Value;

                // A handler on an unnamed control cannot be checked from here,
                // and it also cannot be seeded by name from the code-behind, so
                // it is a defect in its own right rather than something to skip.
                Assert.False(
                    string.IsNullOrEmpty(name),
                    $"{page}: a {element.Name.LocalName} handles LostFocus " +
                    $"({handler}) but has no x:Name, so nothing can seed it");

                found.Add(new WriteBackControl(page, name!, handler));
            }
        }

        return found;
    }

    // Parsed once per page. ShellSource does the resource-by-suffix lookup, the
    // exactly-one assert, and the separator normalisation MSBuild's logical
    // names need; reimplementing those here drifted from it once already.
    private static readonly Dictionary<string, ShellSource> SourceCache = new(StringComparer.Ordinal);

    private static ShellSource SourceFor(string pageXamlName)
    {
        if (SourceCache.TryGetValue(pageXamlName, out var cached)) return cached;

        var source = ShellSource.Load("Settings.Pages." + pageXamlName + ".cs");

        // Parsed with no preprocessor symbols, so a conditionally-compiled
        // region would make the file this reads a different program from the
        // one that ships. Same guard the sibling parity test carries.
        Assert.DoesNotContain("#if", source.Root.ToFullString(), StringComparison.Ordinal);

        SourceCache[pageXamlName] = source;
        return source;
    }

    // The left-hand sides of every assignment in the page, as
    // "Control.Property". Roslyn rather than a string search: a mention in a
    // comment or in a nameof() is not a seed.
    private static HashSet<string> AssignedControlProperties(ShellSource source)
    {
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax identifier,
                } member)
            {
                assigned.Add(identifier.Identifier.ValueText + "." + member.Name.Identifier.ValueText);
            }
        }

        return assigned;
    }

    // Whether the handler compares anything at all. Deliberately loose: what it
    // rules out is the shape that shipped twice, a handler whose only guard is
    // `_loading` and which then writes whatever it is holding.
    private static bool ComparesSomething(ShellSource source, string handlerName) =>
        source.Method(handlerName).DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Any(b => b.OperatorToken.Text is "==" or "!=");

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
                Assert.Fail($"{page} is not well-formed XML: {ex.Message}");
            }
        }

        Assert.True(pages.Count > 0, "no settings page XAML is embedded; see Ghostty.Tests.csproj");
        return pages;
    }
}
