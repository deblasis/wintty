using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The assertion that a span of the seam's source sits inside the file's
/// one <c>#if TESTSEAM</c> region: the guard that says a shipping build
/// carries none of it.
///
/// ShellSource parses with TESTSEAM defined -- it must, or every scan over
/// the seam would be reading an empty file -- so the code is visible to the
/// parse and the directives survive as trivia. Reading the directives back
/// is the only way to check, from a parse that sees the region, that the
/// region exists at all.
///
/// Extracted from <c>TestSeamWiringTests</c> when a second seam test file
/// needed the same rule; one rule, one implementation, so a future change
/// to the gate's shape reddens every pin that leans on it, not whichever
/// file won a copy race.
/// </summary>
internal static class SeamBuildGate
{
    public static void AssertInsideTheBuildGate(
        SyntaxNode root, TextSpan span, string what)
    {
        var directives = root.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)
                        || t.IsKind(SyntaxKind.EndIfDirectiveTrivia)
                        || t.IsKind(SyntaxKind.ElseDirectiveTrivia)
                        || t.IsKind(SyntaxKind.ElifDirectiveTrivia))
            .OrderBy(t => t.SpanStart)
            .ToList();

        var opens = directives
            .Where(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)
                        && ((IfDirectiveTriviaSyntax)t.GetStructure()!)
                            .Condition.ToString() == "TESTSEAM")
            .ToList();
        Assert.True(
            opens.Count == 1,
            $"expected exactly one '#if TESTSEAM' region, found {opens.Count}. "
            + "The seam's absence from a shipping build is what that region is for.");

        // Walk to the #endif that closes it, counting nesting, so an unrelated
        // conditional inside the region cannot be mistaken for the close.
        var open = opens[0];
        var depth = 0;
        TextSpan? close = null;
        foreach (var t in directives.Where(t => t.SpanStart >= open.SpanStart))
        {
            if (t.IsKind(SyntaxKind.IfDirectiveTrivia)) depth++;
            else if (t.IsKind(SyntaxKind.EndIfDirectiveTrivia))
            {
                depth--;
                if (depth == 0) { close = t.Span; break; }
            }
            else if (depth == 1)
            {
                // An #else or #elif would leave a branch this parse cannot
                // see, and ShellSource.Load already refuses that; catching it
                // here names the reason.
                Assert.Fail($"the '#if TESTSEAM' region has an {t.Kind()}, which "
                    + "splits it into a branch the wiring scans cannot read");
            }
        }
        Assert.True(close is not null, "the '#if TESTSEAM' region is never closed");
        Assert.True(
            span.Start > open.Span.End && span.End < close!.Value.Start,
            $"{what} is outside the '#if TESTSEAM' region, so a shipping build "
            + "would still carry it");
    }
}
