using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Settings;

// A settings control that writes to the config file on LostFocus has to be
// seeded from the config file first.
//
// Blur is not an edit. It fires on every pass through the page: tabbing
// through, clicking elsewhere, closing the window. So a box that writes
// unconditionally on blur writes whatever it is currently showing -- and a box
// that was never seeded is showing nothing. AppearancePage's custom-shader box
// was the one page control with no seed, so opening Appearance and moving focus
// past it wrote `custom-shader = ` and dropped a configured shader.
//
// Nothing catches that at runtime: the write is a legitimate config edit, the
// file stays valid, and the terminal just quietly stops using the shader.
//
// The markup is parsed as XML rather than text-scanned, so a handler named
// inside an XML comment does not count, and the code-behind is parsed with
// Roslyn rather than grepped, so the word "ShaderPathBox.Text" in a comment
// does not satisfy the check.
public class SettingsWriteBackSeedingTests
{
    private const string PagePrefix = "Ghostty.Tests.Settings.Pages.";

    // The WinUI project's sources are already embedded under this prefix,
    // keeping their directory in the name, so the code-behind is reached by
    // suffix rather than by adding a second glob that would collide with it.
    private const string SourcePrefix = "Ghostty.Tests.Interop.Sources.Ghostty.";

    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryLostFocusWriteBackControlIsSeededInItsCodeBehind()
    {
        var controls = WriteBackControls();

        // If the scan finds nothing, every assertion below holds vacuously.
        // There is at least one such control in the tree and there has been
        // since the quake key box.
        Assert.True(
            controls.Count > 0,
            "found no LostFocus write-back controls in the settings pages; the " +
            "scan is broken, not the pages");

        var unseeded = new List<string>();
        foreach (var control in controls)
        {
            var assigned = AssignedControlProperties(control.Page);
            if (!assigned.Contains(control.Name + ".Text"))
            {
                unseeded.Add(
                    $"  {control.Page}: {control.Name} writes the config on " +
                    $"{control.Handler} but nothing assigns {control.Name}.Text");
            }
        }

        Assert.True(
            unseeded.Count == 0,
            "settings controls that write on blur without being seeded first. " +
            "Moving focus past one of these writes what it happens to be " +
            "showing, which for an unseeded box is nothing:\n" +
            string.Join("\n", unseeded));
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

    // The left-hand sides of every assignment in the page's code-behind, as
    // "Control.Property". Roslyn rather than a string search: a mention in a
    // comment or in a nameof() is not a seed.
    private static HashSet<string> AssignedControlProperties(string pageXamlName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = pageXamlName + ".cs";
        var matches = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(SourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(suffix, StringComparison.Ordinal))
            .ToList();

        // Exactly one, not "the first". Two pages of the same name in different
        // directories would otherwise be checked against whichever the resource
        // order happened to put first.
        Assert.True(
            matches.Count == 1,
            $"expected exactly one embedded source ending in {suffix}, found " +
            $"{matches.Count}: {string.Join(", ", matches)}");

        using var stream = assembly.GetManifestResourceStream(matches[0]);
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var root = CSharpSyntaxTree.ParseText(reader.ReadToEnd()).GetRoot();

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
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
