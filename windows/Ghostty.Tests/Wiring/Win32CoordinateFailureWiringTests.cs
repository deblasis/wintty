using System;
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
        // Fills a caller-supplied buffer and reports only through the return.
        // A discarded failure reads as "high contrast off" / "no screen
        // reader", which is the confidently-wrong answer in the one subsystem
        // where being wrong is an accessibility defect.
        "SystemParametersInfo",
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
    /// The member-binding arm is the <c>x?.Call()</c> form -- unreachable for
    /// static P/Invokes today, but <c>SyntaxQueries.CalleeText</c> in this same
    /// assembly already handles it, so forgetting it here would be this file
    /// re-learning the lesson it exists to teach.
    ///
    /// A trailing W or A is dropped, so a hand-rolled <c>GetMonitorInfoW</c>
    /// and CsWin32's <c>GetMonitorInfo</c> are one API rather than two entries
    /// that both have to keep matching. Nothing watched here legitimately ends
    /// in W or A.
    /// </summary>
    private static string? SimpleName(InvocationExpressionSyntax call)
    {
        var raw = call.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax bind => bind.Name.Identifier.ValueText,
            _ => null,
        };
        if (raw is null) return null;
        return raw.Length > 1 && (raw[^1] == 'W' || raw[^1] == 'A')
            ? raw[..^1]
            : raw;
    }

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

        // var _ = Call();  -- syntactically unambiguous intent to discard.
        // `var ok = Call();` with ok never read is the case that genuinely
        // needs a semantic model; this one does not.
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Identifier.ValueText: "_" } } => true,

        // Member() => Call();  on anything void-returning consumes nothing.
        // A non-void member returns it to its caller, so only void counts.
        // Methods, local functions and property/indexer setters all have this
        // shape; an accessor has no return type of its own, and a set/init
        // accessor is void by definition.
        ArrowExpressionClauseSyntax arrow => arrow.Parent switch
        {
            MethodDeclarationSyntax { ReturnType: PredefinedTypeSyntax m }
                when m.Keyword.IsKind(SyntaxKind.VoidKeyword) => true,
            LocalFunctionStatementSyntax { ReturnType: PredefinedTypeSyntax l }
                when l.Keyword.IsKind(SyntaxKind.VoidKeyword) => true,
            AccessorDeclarationSyntax acc
                when acc.IsKind(SyntaxKind.SetAccessorDeclaration)
                     || acc.IsKind(SyntaxKind.InitAccessorDeclaration) => true,
            _ => false,
        },

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

    /// <summary>
    /// The tripwire for the blind spot named above.
    ///
    /// <see cref="ShellSource.AllShellSources"/> parses with DEMO, DEBUG and
    /// TESTSEAM defined, so an <c>#else</c>, <c>#elif</c> or <c>#if !</c>
    /// region is invisible to the sweep and a call inside one would be
    /// reported as covered. Recording that in a doc comment is not a guard --
    /// nothing announces the day it stops being hypothetical, which is round
    /// one's finding (a site nobody was looking for) wearing different
    /// clothes. This is the text-level companion
    /// <see cref="ShellSource.ParseForCorpusScan"/> says such a caller owes.
    /// </summary>
    [Fact]
    public void NoShellSource_HidesCodeFromTheCorpusSweep()
    {
        var hidden = new List<string>();
        var scanned = 0;

        foreach (var (tail, text) in ShellSource.AllUnder("Ghostty.Tests.Interop.Sources.Ghostty."))
        {
            scanned++;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("#else", StringComparison.Ordinal)
                    || line.StartsWith("#elif", StringComparison.Ordinal)
                    || line.StartsWith("#if !", StringComparison.Ordinal))
                {
                    hidden.Add($"{tail}:{i + 1}: {line.TrimEnd()}");
                }
            }
        }

        Assert.True(scanned > 0, "the shell-source sweep found no files to read");
        Assert.True(
            hidden.Count == 0,
            "these conditional regions are invisible to every scan built on "
                + "AllShellSources, which parses with DEMO, DEBUG and TESTSEAM defined "
                + "and keeps the opposite branch as disabled trivia. A Win32 call "
                + "discarded inside one would be reported as covered. Either restructure "
                + "so the code is not hidden, or give the affected rules a text-level "
                + "companion and exempt the file here deliberately:\n  "
                + string.Join("\n  ", hidden));
    }
}
