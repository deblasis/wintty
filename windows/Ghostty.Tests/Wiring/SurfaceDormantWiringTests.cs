using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Tier C dormancy's managed wiring. The product trigger is deliberately
/// unwired (the #1051 first-PR scope: machinery in, trigger later), so
/// the test seam op is the machinery's only driver -- the corpus-wide
/// pin holds THAT claim, not just the seam's half of it. The
/// import/export pairing itself is EntryPointParityTests' ground, as
/// with every other crossing.
/// </summary>
public class SurfaceDormantWiringTests
{
    [Fact]
    public void TheSeamIsTheOnlyDriverOfDormancy()
    {
        // Text-level corpus sweep, so disabled conditional regions are
        // seen too (a parse-only sweep is exactly the blind spot the
        // corpus scanner's own docs warn about). The fully-qualified
        // call spells the boundary; the definition side in
        // NativeMethods.cs does not carry the "Interop." receiver.
        var corpus = ShellSource.AllUnder("Ghostty.Tests.Interop.Sources.Ghostty.");
        var drivers = corpus
            .Where(kv => kv.Text.Contains("Interop.NativeMethods.SurfaceGoDormant("))
            .ToList();
        Assert.Single(drivers);
        Assert.EndsWith("Testing.TestSeam.cs", drivers[0].Tail);

        // And that one driver sends exactly once, with one shared
        // readback serving both "go"'s report and "check"'s.
        var seam = ShellSource.Load("Ghostty.Testing.TestSeam.cs");
        Assert.Single(seam.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "Interop.NativeMethods.SurfaceGoDormant"));
        Assert.Single(seam.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "Interop.NativeMethods.SurfaceIsDormant"));
    }

    [Fact]
    public void TheWrappersConvertNativeByteToTrue()
    {
        // The one-bit conversion is the wrapper's whole job; an inverted
        // comparison would flip the seam's "sent" field (a refused freeze
        // reporting sent:true) and pass a mere call-count pin. The
        // wrappers are expression-bodied, so the conversion rides the
        // expression: the native call sits on one side of a != 0.
        var native = ShellSource.Load("Interop.Imports.NativeMethods.cs");
        foreach (var name in new[] { "SurfaceGoDormant", "SurfaceIsDormant" })
        {
            var method = native.Method(name);
            var call = Assert.Single(method.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == name + "Native"));
            var comparison = call.Ancestors()
                .OfType<BinaryExpressionSyntax>().FirstOrDefault();
            Assert.NotNull(comparison);
            Assert.Equal("!=", comparison!.OperatorToken.ValueText);
            Assert.Equal("0", comparison.Right.ToString());
        }
    }
}
