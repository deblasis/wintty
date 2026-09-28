using System.Reflection;
using Ghostty.Core.Gaps;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Gaps;

/// <summary>
/// The gap catalogue is closed: keys are created in <see cref="GapCatalogue"/>
/// only, never from runtime text, so every report of one missing feature
/// shares one token and nothing a plugin writes can become a key or a title.
/// </summary>
public class ClosedCatalogueTests
{
    private const string SourcesPrefix = "Ghostty.Tests.Interop.Sources.";

    [Fact]
    public void GapKey_is_constructed_only_in_the_catalogue()
    {
        var asm = Assembly.GetExecutingAssembly();
        var sources = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(SourcesPrefix, StringComparison.Ordinal) && n.EndsWith(".cs", StringComparison.Ordinal))
            .ToList();

        // Non-vacuity: the scan must see the app and Core sources, the catalogue among them.
        Assert.True(sources.Count > 100, $"only {sources.Count} embedded sources found");
        Assert.Contains(sources, n => n.EndsWith("GapCatalogue.cs", StringComparison.Ordinal));

        var offenders = new List<string>();
        var catalogueCreations = 0;
        foreach (var name in sources)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var tree = CSharpSyntaxTree.ParseText(reader.ReadToEnd());
            var creations = CountGapKeyCreations(tree.GetRoot());
            if (creations == 0) continue;
            if (name.EndsWith("GapCatalogue.cs", StringComparison.Ordinal))
                catalogueCreations += creations;
            else
                offenders.Add(name);
        }

        Assert.Empty(offenders);
        Assert.Equal(GapCatalogue.All.Count, catalogueCreations);
    }

    private static int CountGapKeyCreations(SyntaxNode root)
    {
        var count = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Count(o => o.Type.ToString() is "GapKey" or "Gaps.GapKey" or "Ghostty.Core.Gaps.GapKey");

        // Target-typed `new(...)` assigned to something declared as GapKey.
        foreach (var implicitNew in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            var declared = implicitNew.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault()?.Type
                ?? implicitNew.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Type;
            if (declared?.ToString() is "GapKey") count++;
        }
        return count;
    }
}
