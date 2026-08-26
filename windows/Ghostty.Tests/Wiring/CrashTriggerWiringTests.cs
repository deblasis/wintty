using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That the crash triggers are reachable from both front doors, in every
/// build.
///
/// The availability guards are text-level, which is the exception
/// <c>ShellSource</c> describes rather than a shortcut. Their claim is about
/// preprocessor directives themselves: that the trigger and the palette
/// source contain none. A parse cannot make that claim, because which half of
/// a conditional it can see is decided by the symbols it defines, and the
/// regions are resolved away before any rule could read them. The lines are
/// what carry the claim, so the lines are what is read.
///
/// The palette guards at the bottom are parsed, because their claim is the
/// opposite kind: which expression guards which, where a substring match
/// cannot tell a live guard from one inside a comment.
///
/// The direction is deliberate. These guards used to assert the opposite,
/// that the triggers were Debug-only. Capture has to be provable in the
/// build users install, and a trigger compiled out of Release leaves the one
/// configuration that matters as the one nobody can test.
///
/// What this cannot see: whether a case arm does what its comment says, and
/// whether the palette rows reach a live surface. Neither is observable
/// without running a build that is allowed to die.
/// </summary>
public class CrashTriggerWiringTests
{
    private const string ShellPrefix = "Ghostty.Tests.Interop.Sources.Ghostty.";

    private static string ShellText(string tail)
    {
        var matches = ShellSource.AllUnder(ShellPrefix)
            .Where(f => f.Tail == tail)
            .ToList();
        Assert.True(
            matches.Count == 1,
            $"expected exactly one embedded shell source '{tail}', found {matches.Count}");
        return matches[0].Text;
    }

    // -- One implementation, one catalogue -------------------------------

    [Fact]
    public void CrashTrigger_DispatchesOffTheCatalogue()
    {
        // The whole point of CrashKinds is that neither front door keeps its
        // own list. A trigger that went back to matching the raw argument
        // would let a kind exist for the CLI and not the palette.
        Assert.Contains("CrashKinds.Find(kind)", ShellText("Cli.CrashTrigger.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInProcessKind_HasItsOwnArm()
    {
        // A catalogue entry with no mechanism reaches the default arm and
        // exits 2, which looks like an unknown kind rather than a gap.
        var text = ShellText("Cli.CrashTrigger.cs");
        foreach (var kind in CrashKinds.All.Where(k => !k.NeedsSurface))
        {
            Assert.Contains($"case \"{kind.Id}\":", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoSurfaceBoundKind_AlsoHasAnArm()
    {
        // A surface-bound kind must go through the binding action and
        // nothing else. A second mechanism behind the same id is how a probe
        // ends up reporting on a layer it did not touch: a managed fault
        // dressed up as a libghostty one.
        var text = ShellText("Cli.CrashTrigger.cs");
        foreach (var kind in CrashKinds.All.Where(k => k.NeedsSurface))
        {
            Assert.DoesNotContain($"case \"{kind.Id}\":", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PaletteSource_ProjectsTheCatalogue()
    {
        var text = ShellText("Commands.CrashCommandSource.cs");
        Assert.Contains("CrashKinds.All", text, StringComparison.Ordinal);
        // Its own category, so grouped mode sorts them away from the
        // commands a user reaches for.
        Assert.Contains("CommandCategory.Debug", text, StringComparison.Ordinal);
        // And no second opinion about what a kind does: the row hands the id
        // back to the one implementation.
        Assert.DoesNotContain("Environment.FailFast", text, StringComparison.Ordinal);
    }

    // -- Availability ----------------------------------------------------
    //
    // These guards were inverted deliberately. They used to assert the
    // triggers were Debug-only. Capture has to be provable in the build
    // users actually install, and a trigger compiled out of Release leaves
    // the one configuration that matters as the one nobody can test. So the
    // invariant now is the opposite: the triggers must reach every build.

    [Fact]
    public void PaletteSource_IsNotBuildGated()
    {
        var lines = ShellText("Commands.CrashCommandSource.cs")
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        Assert.DoesNotContain(lines, l => l.StartsWith("#if", StringComparison.Ordinal));
        Assert.DoesNotContain("#else", lines);
    }

    [Fact]
    public void TheTriggerItselfIsNotBuildGated()
    {
        // The palette rows are worth nothing if Run() stubs itself out: the
        // entries would appear in a shipped build and quietly do nothing,
        // which is worse than not shipping them.
        var lines = ShellText("Cli.CrashTrigger.cs")
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        Assert.DoesNotContain(lines, l => l.StartsWith("#if", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "crash-trigger is compiled out of Release builds",
            ShellText("Cli.CrashTrigger.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NothingGatesThePaletteSourceRegistration()
    {
        // Swept over the whole shell rather than over MainWindow, because a
        // second registration site added later is exactly the one nobody
        // would think about.
        var sites = new List<string>();
        foreach (var (tail, text) in ShellSource.AllUnder(ShellPrefix))
        {
            foreach (var conditions in EnclosingConditions(text, "new CrashCommandSource("))
            {
                sites.Add(tail);
                Assert.Empty(conditions);
            }
        }

        // An empty sweep is a query that stopped matching, and reads as a
        // pass.
        Assert.Single(sites);
    }

    /// <summary>
    /// For every line containing <paramref name="needle"/>, the stack of
    /// <c>#if</c> conditions enclosing it, outermost first.
    ///
    /// A line inside an <c>#else</c> reports the condition negated, so a
    /// registration that moved into the Release half of a conditional does
    /// not read as gated.
    /// </summary>
    private static List<List<string>> EnclosingConditions(string text, string needle)
    {
        var found = new List<List<string>>();
        var stack = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            // Matched without requiring the trailing space, and closed
            // without requiring an exact line. `#if(DEBUG)` is legal C# and
            // walked straight through a `"#if "` prefix test, leaving the
            // registration reading as ungated while Release lost it. So did
            // `#endif // DEBUG`, in the other direction: the condition stayed
            // on the stack and every later line read as gated.
            if (trimmed.StartsWith("#if", StringComparison.Ordinal))
            {
                stack.Add(trimmed[3..].Trim());
                continue;
            }
            if (trimmed.StartsWith("#elif", StringComparison.Ordinal) && stack.Count > 0)
            {
                stack[^1] = trimmed[5..].Trim();
                continue;
            }
            if (trimmed.StartsWith("#else", StringComparison.Ordinal) && stack.Count > 0)
            {
                stack[^1] = "!(" + stack[^1] + ")";
                continue;
            }
            if (trimmed.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            // Comments mentioning the type are not registrations. Only a
            // line that is not a comment counts.
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.Contains(needle, StringComparison.Ordinal))
                found.Add([.. stack]);
        }

        return found;
    }

    // -- Keeping a destructive row out of reach ---------------------------
    //
    // These triggers ship in Release, so the compiler is not what stands
    // between a user and a deliberate crash. Two properties of the palette
    // are, and both were absent when the rows first landed: a fresh profile,
    // the query "select", and Enter took the window down, because Debug rows
    // matched on their description ("...this build selected") and the default
    // ordering ignored Category entirely.
    //
    // Parsed, not text: the claim is about which expressions guard which, and
    // a substring match cannot tell a live guard from one inside a comment.

    [Fact]
    public void DebugRows_MatchOnTitleOnly()
    {
        var filter = ShellSource
            .Load("Commands.CommandPaletteViewModel.cs")
            .Method("ApplyFilter");

        var descriptionMatches = filter.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith(
                "Description.Contains", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            descriptionMatches.Count > 0,
            "ApplyFilter no longer matches on Description at all. If that is "
                + "deliberate, delete this test; it exists to prove the match "
                + "excludes Debug rows, not to require the match.");

        foreach (var match in descriptionMatches)
        {
            var guarded = match.Ancestors()
                .OfType<BinaryExpressionSyntax>()
                .Any(b => b.ToString().Contains(
                    "Category != CommandCategory.Debug", StringComparison.Ordinal));
            Assert.True(
                guarded,
                "a Description match in ApplyFilter is not behind "
                    + "'Category != CommandCategory.Debug'. Debug rows describe "
                    + "what they destroy in ordinary words, so matching their "
                    + "descriptions puts 'crash the renderer' in front of "
                    + "someone who typed 'select'.");
        }
    }

    [Fact]
    public void DebugRows_SortLastInBothBranches()
    {
        var filter = ShellSource
            .Load("Commands.CommandPaletteViewModel.cs")
            .Method("ApplyFilter");

        // Every ordering chain built in this method, whether or not grouping
        // is on. The grouped branch sorts by Category directly; the other has
        // to name Debug explicitly, and used not to.
        var chains = filter.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith(".OrderBy", StringComparison.Ordinal)
                || i.Expression.ToString().EndsWith(".OrderByDescending", StringComparison.Ordinal))
            .ToList();
        Assert.True(chains.Count >= 2, $"expected both ordering branches, found {chains.Count}");

        foreach (var chain in chains)
        {
            var key = chain.ArgumentList.Arguments.ToString();
            var demotesDebug =
                key.Contains("c.Category", StringComparison.Ordinal)
                && (key.Contains("CommandCategory.Debug", StringComparison.Ordinal)
                    || key.Trim() == "c => c.Category");
            Assert.True(
                demotesDebug,
                $"an ordering branch leads with '{key}', which does not put "
                    + "Debug last. Frecency cannot be the first key for these: "
                    + "executing one records the use before the process dies, "
                    + "so one accident promotes that row for every later launch.");
        }
    }

}
