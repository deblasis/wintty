using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The half of the tab-naming rule that lives in the shell.
///
/// The model knows what to do with a launch process once it has one
/// (<c>TabLaunchNameTests</c>); what it cannot know is whether anything
/// ever gives it one. Only the WinUI layer holds the pid -- the pane
/// reports it, <c>MainWindow</c> polls for it, <c>App</c> is where it
/// lands -- and without the resolution at that seam a tab opened with no
/// profile falls all the way to the generic name and stays there. That is
/// the regression this guards: the model's tests would stay green through
/// it, because they hand the name over themselves.
///
/// <c>App</c> lives in the WinUI project, which this assembly cannot
/// reference, so this parses the source the way the other wiring guards
/// do.
/// </summary>
public class PaneLaunchNameWiringTests
{
    private const string ShellFile = "App.xaml.cs";
    private const string Namer = "NameTabAfterItsLaunchProcess";
    private const string Resolve =
        "Ghostty.Core.Profiles.Tracking.PaneLaunchImage.TryResolve";
    private const string Report = "tab.OnPaneLaunched";

    private static ShellSource App() => ShellSource.Load(ShellFile);

    /// <summary>
    /// Both places a tab's shell pid becomes known reach the namer: the
    /// late path, which is the one that fires on a cold start after the
    /// poll, and the enrolment path, which catches a pid already set.
    /// Wiring only the one that fires today leaves the other silently
    /// unnamed the day it starts firing.
    /// </summary>
    [Theory]
    [InlineData("OnTabShellPidChanged")]
    [InlineData("RegisterTabForProcessTracking")]
    public void EveryPathThatLearnsTheShellPid_NamesTheTabFromIt(string handler)
    {
        App().Method(handler).Body!.Call(Namer);
    }

    /// <summary>
    /// And reaches it without passing an early return about the
    /// active-process tracker.
    ///
    /// Naming a tab after what runs in it is not a function of
    /// foreground-process TRACKING; they share a pid and nothing else.
    /// Both handlers used to open with <c>if (_activeProcessTracker is
    /// null) return;</c>, which would have made a tab's name depend on a
    /// subsystem that has its own per-profile opt-out. The call-site
    /// assertion above cannot see this: it finds the invocation whether or
    /// not anything reaches it.
    /// </summary>
    [Theory]
    [InlineData("OnTabShellPidChanged")]
    [InlineData("RegisterTabForProcessTracking")]
    public void TheNaming_IsNotGatedOnTheProcessTracker(string handler)
    {
        var call = App().Method(handler).Body!.Call(Namer);

        var gate = call.Ancestors().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("_activeProcessTracker"));
        Assert.True(
            gate is null,
            $"{handler} reaches the namer only inside a test on the active-process "
                + $"tracker, at: {gate?.Condition}");

        // Not after one either: a `return` above the call is the same gate
        // written as a guard clause, which is how it was written before.
        var body = App().Method(handler).Body!;
        var gateBefore = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("_activeProcessTracker"))
            .Where(i => i.Statement.ToString().Contains("return"))
            .Any(i => i.SpanStart < call.SpanStart);
        Assert.False(
            gateBefore,
            $"{handler} returns on the active-process tracker before it names the tab, "
                + "so a tab's name now depends on whether foreground tracking is up");
    }

    /// <summary>
    /// The resolve opens ONE handle and reads both facts through it.
    ///
    /// Not a tidiness point. The kernel will not recycle a pid while a
    /// handle to it is open, so the single handle is what makes the second
    /// read provably about the same process as the first; two opens by pid
    /// would put a reuse window between them, and the comment claiming
    /// otherwise would be the only thing standing there. Reading the
    /// command line through the pid-taking overload is exactly that
    /// regression, and it is one character of difference.
    /// </summary>
    [Fact]
    public void TheResolve_OpensOneHandle_AndReadsBothFactsThroughIt()
    {
        var body = ShellSource.Load("Core.Profiles.Tracking.PaneLaunchImage.cs")
            .Method("TryResolve").Body!;

        Assert.Single(body.Calls("DWritePInvoke.OpenProcess"));
        Assert.Single(body.Calls("DWritePInvoke.CloseHandle"));
        Assert.Single(body.Calls("NtProcessInterop.GetCommandLine"));
        Assert.Empty(body.Calls("NtProcessInterop.TryGetCommandLine"));
    }

    [Fact]
    public void TheNamer_ResolvesThePidAndReportsItToTheModel()
    {
        var body = App().Method(Namer).Body!;

        body.Call(Resolve);
        body.Call(Report);
    }

    /// <summary>
    /// It asks about THE TAB'S pid, the one handed to it.
    ///
    /// Nothing else in this file pins the input. A namer that resolved
    /// <c>Environment.ProcessId</c> instead satisfies every call-site
    /// assertion here and every test in the model, and names every tab
    /// with no profile after this application's own executable -- which
    /// is the defect the whole PR exists to remove, reached by a
    /// different road and through a code path where the product name is
    /// the honest answer, so no assertion about the STRING can catch it
    /// either.
    /// </summary>
    [Fact]
    public void TheNamer_AsksAboutThePidItWasGiven()
    {
        var method = App().Method(Namer);
        var pidParameter = method.ParameterList.Parameters
            .Single(p => p.Type!.ToString() == "int")
            .Identifier.ValueText;

        var asked = method.Body!.Call(Resolve).ArgExpression(0);
        var named = asked.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .ToList();

        Assert.True(
            named.Contains(pidParameter),
            $"the resolve is asked about '{asked}', which does not read the '{pidParameter}' "
                + "parameter. A namer that resolves this process's own id passes every "
                + "other assertion here and names every profile-less tab after the "
                + "application.");
    }

    /// <summary>
    /// And it reports what it resolved, rather than resolving one thing
    /// and reporting another.
    /// </summary>
    [Fact]
    public void TheNamer_ReportsWhatItResolved()
    {
        var body = App().Method(Namer).Body!;

        // The resolve binds two names; the report has to pass those two.
        var binding = body.Call(Resolve)
            .Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        Assert.True(
            binding is not null,
            "the resolve is no longer assigned to anything, so this test cannot tell "
                + "what the report is given");

        var bound = binding!.Left.DescendantNodesAndSelf()
            .OfType<SingleVariableDesignationSyntax>()
            .Select(d => d.Identifier.ValueText)
            .ToList();
        Assert.Equal(2, bound.Count);

        var reported = body.Call(Report).ArgumentList.Arguments
            .Select(a => a.Expression.ToString())
            .ToList();
        Assert.Equal(bound, reported);
    }

    /// <summary>
    /// A tab a profile already NAMES is not asked about: the answer would
    /// be a string nothing renders, bought with a handle open and two
    /// queries on the UI thread, once per tab. The guard is the only thing
    /// keeping that cost off the common path.
    ///
    /// What it must NOT be is a guard on the snapshot merely existing.
    /// <c>TabModel.Compose</c> coalesces the profile's display name on
    /// whitespace, so a profile with a blank name falls through to the
    /// launch name by design; a guard that skipped it because a snapshot
    /// was attached would strand exactly that tab on the generic word, and
    /// no test of the model could see it.
    /// </summary>
    [Fact]
    public void TheNamer_AsksNothing_ForATabAProfileAlreadyNames()
    {
        var body = App().Method(Namer).Body!;

        var guard = body.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("ProfileSnapshot"));

        Assert.True(
            guard is not null,
            "NameTabAfterItsLaunchProcess no longer returns early for a tab a profile "
                + "names, so every profile tab now pays for a launch name the label "
                + "will never read");
        Assert.Contains("return", guard!.Statement.ToString());

        // Polarity, on the node rather than on its text. A substring
        // assertion accepts its own negation, and an inverted guard here
        // is not a cost regression but total feature loss: every tab with
        // no profile -- the only case this exists for -- would return
        // before resolving, and every tab with one would pay for a name
        // nothing renders.
        var negation = Assert.IsType<PrefixUnaryExpressionSyntax>(guard.Condition);
        Assert.True(
            negation.IsKind(SyntaxKind.LogicalNotExpression),
            $"the guard's condition is not a negation, at: {guard.Condition}");

        var blankCheck = negation.Operand.AssertCallTo("string.IsNullOrWhiteSpace");
        var subject = blankCheck.Arg(0);
        Assert.True(
            subject.Contains("DisplayName"),
            "the guard asks about the snapshot rather than the name it carries, at: "
                + subject
                + ". A profile whose display name is blank falls through to the launch "
                + "name in Compose; this guard would stop such a tab ever getting one.");

        AssertGuardsTheResolve(guard, "the profile-name guard");
    }

    /// <summary>
    /// A cost guard that runs AFTER the thing it guards is not a guard.
    /// Existence and polarity both survive moving it down, and the cost it
    /// exists to keep off the common path comes straight back.
    /// </summary>
    private static void AssertGuardsTheResolve(IfStatementSyntax guard, string what)
    {
        var resolve = App().Method(Namer).Body!.Call(Resolve);
        Assert.True(
            guard.SpanStart < resolve.SpanStart,
            $"{what} sits below the resolve it guards, so every tab it is meant to "
                + "spare pays for the handle open and the two queries anyway");
    }

    /// <summary>
    /// And it stops asking only once the model says the name it holds is
    /// the complete one. Guarding on the name merely existing would latch
    /// the lesser answer a half-read gives ("WSL" where "WSL: Ubuntu-24.04"
    /// was a moment away) and never look again.
    /// </summary>
    [Fact]
    public void TheNamer_KeepsAsking_UntilTheAnswerIsComplete()
    {
        var guards = App().Method(Namer).Body!
            .DescendantNodes().OfType<IfStatementSyntax>()
            .ToList();

        var complete = guards.FirstOrDefault(
            g => g.Condition.ToString().Contains("PaneLaunchNameIsComplete"));
        Assert.True(
            complete is not null,
            "the namer no longer stops once the model has a complete answer, so every "
                + "shell pid change pays for a handle open and two queries");

        // Unnegated, for the same reason the guard above is asserted
        // negated: `!IsComplete` would resolve only for the tab that has
        // already finished, and never for the one that has not.
        Assert.False(
            complete!.Condition is PrefixUnaryExpressionSyntax,
            $"the completeness guard is inverted, at: {complete.Condition}");
        Assert.Contains("return", complete.Statement.ToString());

        Assert.DoesNotContain(
            guards.Select(g => g.Condition.ToString()),
            c => c.Contains("PaneLaunchName is") || c.Contains("PaneLaunchName =="));

        AssertGuardsTheResolve(complete, "the completeness guard");
    }
}
