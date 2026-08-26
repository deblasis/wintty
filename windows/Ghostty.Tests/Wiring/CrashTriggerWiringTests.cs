using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Diagnostics;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That the crash triggers are reachable from both front doors, and only
/// from a Debug build.
///
/// Text-level rather than parsed, which is the exception <c>ShellSource</c>
/// describes rather than a shortcut. The trigger's real body and the whole
/// palette source live inside <c>#if DEBUG</c>, and the half a parse cannot
/// see is decided by which symbols the parse defines; a rule about "is this
/// inside a DEBUG region" cannot be written against a tree that has already
/// resolved the regions away. The lines are what carry the claim, so the
/// lines are what is read.
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
            .Split('
')
            .Select(l => l.TrimEnd('').Trim())
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
            .Split('
')
            .Select(l => l.TrimEnd('').Trim())
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

            if (trimmed.StartsWith("#if ", StringComparison.Ordinal))
            {
                stack.Add(trimmed[4..].Trim());
                continue;
            }
            if (trimmed.StartsWith("#elif ", StringComparison.Ordinal) && stack.Count > 0)
            {
                stack[^1] = trimmed[6..].Trim();
                continue;
            }
            if (trimmed == "#else" && stack.Count > 0)
            {
                stack[^1] = "!(" + stack[^1] + ")";
                continue;
            }
            if (trimmed == "#endif" && stack.Count > 0)
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
}
