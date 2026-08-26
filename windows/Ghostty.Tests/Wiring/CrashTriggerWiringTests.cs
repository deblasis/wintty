using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Diagnostics;
using Microsoft.CodeAnalysis;
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
    public void TheCliFrontDoorIsNotBuildGated()
    {
        // Program.cs, not just CrashTrigger.cs. The line-level scans above
        // cover the trigger and the palette source; the `+crash` interception
        // that reaches the trigger was covered by nothing. Wrapping that one
        // `if` in `#if DEBUG` compiles `+crash` out of Release, makes
        // crash-matrix.ps1 unrunnable against the shipped build, and left
        // every test in this file green.
        //
        // Text, not a parse, for the reason at the top of this file: which
        // half of a conditional a parse can see depends on the symbols it
        // defines, and the claim here is about the directives themselves.
        var text = ShellText("Program.cs");
        var needle = "args[0] == \"+crash\"";
        Assert.Contains(needle, text, StringComparison.Ordinal);

        foreach (var conditions in EnclosingConditions(text, needle))
        {
            Assert.Empty(conditions);
        }
    }

    [Fact]
    public void NoMSBuildConditionRemovesTheCrashSourcesFromABuild()
    {
        // The scans in this file read EMBEDDED resources, and the embedding
        // is an unconditional wildcard over ..\Ghostty\**\*.cs. So a
        // <Compile Remove> under a Configuration condition takes the triggers
        // out of the shipped build without touching a single line those scans
        // can see. Not hypothetical: Ghostty.csproj already does exactly that
        // for Demo\**\*.cs, and this project's own comments describe the
        // mismatch.
        var csproj = ShellSource.AllUnder(ShellPrefix)
            .Any(f => f.Tail == "Program.cs");
        Assert.True(csproj, "the shell corpus is empty; this test proves nothing");

        var text = System.IO.File.ReadAllText(ShellProjectPath);
        foreach (var name in new[]
                 {
                     "Cli\\CrashTrigger.cs",
                     "Commands\\CrashCommandSource.cs",
                     "Diagnostics\\CrashKinds.cs",
                 })
        {
            Assert.DoesNotContain(
                $"Compile Remove=\"{name}\"",
                text,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The shell project file, located from the test assembly rather than
    /// embedded, because the claim is about the build rules themselves.
    /// </summary>
    private static string ShellProjectPath
    {
        get
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = System.IO.Path.Combine(
                    dir.FullName, "windows", "Ghostty", "Ghostty.csproj");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new System.IO.FileNotFoundException(
                "could not locate windows/Ghostty/Ghostty.csproj from "
                + AppContext.BaseDirectory);
        }
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

        // Every field a query is matched against EXCEPT Title. Title is the
        // one a Debug row is allowed to match on, which is the whole rule.
        //
        // Classified by the property the call hangs off, not by the callee
        // text: `c.Subtitle?.Contains(...)` is a conditional access, so the
        // invocation's own Expression is a bare `.Contains` and matching on
        // "Subtitle?.Contains" silently found nothing.
        // By the invoked member's name, not by the node's text. The enclosing
        // `Where(...)` is itself an invocation and stringifies to the whole
        // lambda, so a text match picked it up and then reported it as an
        // unguarded Contains.
        var matches = filter.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => CalleeName(i.Expression) == "Contains")
            .Select(i => (Node: (SyntaxNode)i, Owner: MatchOwner(i)))
            .ToList();

        var secondaryMatches = matches
            .Where(m => !m.Owner.StartsWith("c.Title", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            secondaryMatches.Count >= 3,
            "ApplyFilter no longer matches on Description, Subtitle and "
                + "ActionKey. If that is deliberate, delete this test; it "
                + $"exists to prove those matches exclude Debug rows. Found "
                + $"{secondaryMatches.Count}.");

        // The guard, located exactly: the '&&' whose LEFT side is the Debug
        // check. Anything the rule protects has to sit in its RIGHT side.
        //
        // The earlier version walked every BinaryExpressionSyntax ancestor and
        // passed if ANY of them stringified to something containing the Debug
        // check. The whole lambda body is one '||' chain, so the outermost
        // expression always contains it, and the test passed no matter where
        // the match sat. Hoisting the Description match up a level, which is
        // precisely the regression, left it green.
        var debugGuards = filter.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Where(b => b.OperatorToken.Text == "&&"
                && b.Left.ToString().Replace(" ", "")
                    == "c.Category!=CommandCategory.Debug")
            .ToList();
        Assert.True(
            debugGuards.Count == 1,
            $"expected exactly one 'c.Category != CommandCategory.Debug &&' "
                + $"guard in ApplyFilter, found {debugGuards.Count}. The tests "
                + "below locate the protected matches by it.");
        var guardedRegion = debugGuards[0].Right;

        foreach (var match in secondaryMatches)
        {
            Assert.True(
                guardedRegion.Contains(match.Node),
                $"'{match.Owner}' in ApplyFilter is not inside the right side "
                    + "of 'c.Category != CommandCategory.Debug &&'. Debug rows "
                    + "describe what they destroy in ordinary words, so "
                    + "matching their descriptions puts 'crash the renderer' "
                    + "in front of someone who typed 'select'.");
        }
    }

    /// <summary>
    /// The `c.&lt;Property&gt;...` expression a Contains call hangs off, found by
    /// walking up rather than by reading the callee, so a conditional access
    /// is classified the same way a plain member access is.
    /// </summary>
    /// <summary>
    /// The name of the member being invoked, for a plain member access
    /// (<c>c.Title.Contains</c>) or a conditional one (<c>?.Contains</c>).
    /// Null for anything else.
    /// </summary>
    private static string? CalleeName(ExpressionSyntax callee) => callee switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        MemberBindingExpressionSyntax m => m.Name.Identifier.Text,
        _ => null,
    };

    private static string MatchOwner(SyntaxNode node)
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
        {
            var text = cur.ToString();
            if (text.StartsWith("c.", StringComparison.Ordinal)) return text;
        }

        return node.ToString();
    }

    [Fact]
    public void TheUnfilteredList_ExcludesDebugRows()
    {
        var filter = ShellSource
            .Load("Commands.CommandPaletteViewModel.cs")
            .Method("ApplyFilter");

        // Sorting Debug last governs position, and title-only matching needs a
        // query, so neither reaches the empty-query branch. Without an
        // exclusion here, opening the palette and pressing End then Enter
        // takes the window down on a shipped build with no confirmation.
        var emptyBranch = filter.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString()
                .Contains("IsNullOrEmpty", StringComparison.Ordinal));
        Assert.True(
            emptyBranch is not null,
            "ApplyFilter no longer has an empty-query branch; this test "
                + "locates the unfiltered list by it.");

        Assert.Contains(
            "c.Category != CommandCategory.Debug",
            emptyBranch!.Statement.ToString().Replace("  ", " "));
    }

    [Fact]
    public void DebugIsLastInTheCategoryEnum()
    {
        // The grouped ordering branch sorts by Category directly, so its
        // entire correctness rests on Debug being the last member. Nothing
        // else asserts it, and moving Debug above Demo silently puts crash
        // rows at the top of every grouped list.
        var text = ShellText("Commands.CommandItem.cs");
        var body = text[text.IndexOf("enum CommandCategory", StringComparison.Ordinal)..];
        body = body[..body.IndexOf('}')];

        var members = body
            .Split(',', '\n')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0
                && !s.StartsWith("//", StringComparison.Ordinal)
                && !s.StartsWith("enum", StringComparison.Ordinal)
                && !s.StartsWith("{", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(members);
        Assert.Equal("Debug", members[^1]);
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

        // Exact shapes, not substrings. The earlier version asked whether the
        // key text mentioned c.Category and CommandCategory.Debug, which is
        // as true of `? 0 : 1` as of `? 1 : 0`, and it accepted
        // OrderByDescending(c => c.Category) because that trims to the same
        // string as the ascending form. Both inversions put Debug FIRST and
        // both left the test green.
        //
        // Brittle on purpose. A reformulated guard on destructive rows should
        // stop and make someone look, not pattern-match its way through.
        foreach (var chain in chains)
        {
            var ascending = chain.Expression.ToString()
                .EndsWith(".OrderBy", StringComparison.Ordinal);
            var key = chain.ArgumentList.Arguments.ToString().Replace(" ", "");

            var demotesDebug = ascending && (
                // Ungrouped: name Debug explicitly, sort it after the rest.
                key == "c=>c.Category==CommandCategory.Debug?1:0"
                // Grouped: sort by the enum, which puts Debug last only
                // because it is the last member. DebugIsLastInTheCategoryEnum
                // is what holds up this half.
                || key == "c=>c.Category");

            Assert.True(
                demotesDebug,
                $"an ordering branch orders {(ascending ? "ascending" : "descending")} "
                    + $"by '{key}', which is not one of the two shapes known to put "
                    + "Debug last. Frecency cannot be the first key for these: "
                    + "executing one records the use before the process dies, so one "
                    + "accident promotes that row for every later launch. If this is "
                    + "a deliberate reformulation, verify it demotes Debug and add "
                    + "its shape here.");
        }
    }

}
