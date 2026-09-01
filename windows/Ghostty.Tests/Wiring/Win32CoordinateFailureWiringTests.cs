using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Win32 calls whose failure is indistinguishable from success: they signal
/// through the return value and leave the point or rect they were handed
/// untouched, so a discarded result turns unconverted input into confidently
/// wrong output. <c>ClientToScreen</c> is the one that bit us (issue #896):
/// client coordinates returned labelled as screen ones, at two seam sites
/// that decide which pixels a harness samples.
///
/// A corpus rule rather than named lines, because the defect was that another
/// call site existed and nobody was looking for it.
///
/// Matching is on the callee's SIMPLE NAME, not on a <c>PInvoke.</c> prefix.
/// This codebase uses two Win32 idioms -- CsWin32's generated <c>PInvoke</c>
/// class and hand-rolled <c>[LibraryImport]</c> partials -- and a rule that
/// saw only the first would have been blind to
/// <c>TrayIconService.ShowContextMenu</c>, which discarded a
/// <c>GetCursorPos</c> and fed the untouched point straight to
/// <c>TrackPopupMenu</c>. That is the same defect this file exists for, in a
/// spelling the first version of this rule could not see.
///
/// KNOWN BLIND SPOT: <see cref="ShellSource.AllShellSources"/> parses with
/// DEMO, DEBUG and TESTSEAM defined and, unlike <see cref="ShellSource.Load"/>,
/// does not refuse a tree with regions it cannot see. A call inside an
/// <c>#else</c> or <c>#if !DEBUG</c> would therefore be invisible here. No
/// such region exists in the shell today (every conditional is a bare
/// <c>#if</c> on one of those three symbols), which is why this is recorded
/// rather than solved; the day one appears, this rule needs a text-level
/// companion, as <see cref="ShellSource.ParseForCorpusScan"/> spells out.
/// </summary>
public class Win32CoordinateFailureWiringTests
{
    /// <summary>
    /// Calls of this shape that the corpus actually makes. Each must match at
    /// least one call site: a rule enforcing nothing is worse than no rule,
    /// because it reads as coverage.
    /// </summary>
    private static readonly string[] Enforced =
    {
        "ClientToScreen",
        "GetWindowRect",
        "GetCursorPos",
        "GetWindowPlacement",
        "GetMonitorInfo",
        "GetMonitorInfoW",
    };

    /// <summary>
    /// Same shape, not used here yet. Listed so the rule is already in force
    /// the day one appears, and kept separate from <see cref="Enforced"/> so
    /// their absence cannot be mistaken for coverage.
    /// </summary>
    private static readonly string[] Forward =
    {
        "ScreenToClient",
        "GetClientRect",
        "MapWindowPoints",
    };

    /// <summary>
    /// The callee's simple name, receiver ignored: <c>PInvoke.GetCursorPos</c>
    /// and a bare <c>GetCursorPos</c> are the same API and the same hazard.
    /// </summary>
    private static string? SimpleName(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Whether the result is thrown away. Three unambiguous forms; a fourth
    /// (<c>var ok = Call(); // never read</c>) needs a semantic model and is
    /// not covered, so what this proves is that the value was CONSUMED, not
    /// that it was acted on. A lambda body is not treated as a discard either,
    /// because whether the delegate returns the bool cannot be told from
    /// syntax alone.
    /// </summary>
    private static bool ResultIsDiscarded(InvocationExpressionSyntax call) => call.Parent switch
    {
        // Call();
        ExpressionStatementSyntax => true,

        // _ = Call();  -- an assignment, but to nothing.
        AssignmentExpressionSyntax assign
            when assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
                 && assign.Left is IdentifierNameSyntax { Identifier.ValueText: "_" } => true,

        // void Member() => Call();  -- an expression body on a void-returning
        // member consumes nothing. A non-void one returns it to its caller.
        ArrowExpressionClauseSyntax arrow
            when arrow.Parent is MethodDeclarationSyntax { ReturnType: PredefinedTypeSyntax rt }
                 && rt.Keyword.IsKind(SyntaxKind.VoidKeyword) => true,

        _ => false,
    };

    [Fact]
    public void NoSilentCoordinateConversion_HasItsFailureDiscarded()
    {
        var watched = Enforced.Concat(Forward).ToHashSet();
        var discarded = new List<string>();
        var hits = watched.ToDictionary(n => n, _ => 0);

        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (SimpleName(call) is not { } name || !watched.Contains(name)) continue;
                hits[name]++;

                if (ResultIsDiscarded(call))
                    discarded.Add($"{resource}: {call}");
            }
        }

        // Per name, not in aggregate. A single total hides a rule that stopped
        // matching: five names finding nothing while the sixth finds four
        // still leaves the count comfortably positive.
        var silent = Enforced.Where(n => hits[n] == 0).ToList();
        Assert.True(
            silent.Count == 0,
            "these calls are declared enforced but match no call site, so their rule "
                + "proves nothing -- either the corpus stopped making them (move them to "
                + $"Forward) or the match stopped working: {string.Join(", ", silent)}");

        Assert.True(
            discarded.Count == 0,
            "these Win32 calls signal failure only through their return value, and "
                + "leave their point/rect untouched when they fail -- so a discarded "
                + "result is unconverted coordinates reported as converted:\n  "
                + string.Join("\n  ", discarded));
    }
}
