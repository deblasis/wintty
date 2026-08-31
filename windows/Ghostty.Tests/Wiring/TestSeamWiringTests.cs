using System.Linq;
using Ghostty.Tests.Wiring;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The test seam's opt-in is its only safety property, so the spike pins the
/// two facts that keep it true: the gate reads before any pipe exists, and
/// the pipe's life is bounded by the window that opened it. The full fact
/// campaign waits for the seam to harden; these are the two that cannot be
/// allowed to rot quietly.
///
/// What they can catch: the gate inverted or widened, the server started
/// unconditionally, the window closing without the server dying, and a
/// command bypassing the UI-thread marshal.
///
/// What they cannot catch: whether a command really drives the same handlers
/// the pointer path does. That is what the seam acceptance script proves
/// against the running app.
/// </summary>
public class TestSeamWiringTests
{
    private static void CtorCallsTestSeamStart()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var calls = window.Root.Calls("Testing.TestSeam.Start");
        Assert.True(
            calls.Count == 1,
            $"expected exactly one TestSeam.Start call in MainWindow.xaml.cs, " +
            $"found {calls.Count}");

        var ctor = window.Root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == "MainWindow"
                        && c.Span.Contains(calls[0].Span))
            .ToList();
        Assert.True(
            ctor.Count == 1,
            "TestSeam.Start is not called from exactly one MainWindow constructor");
    }

    [Fact]
    public void TheSeamIsGated_OnAnExplicitOptInEnvValue()
    {
        CtorCallsTestSeamStart();

        var source = ShellSource.Load("Testing.TestSeam.cs");
        var start = source.Method("Start");
        var reads = start.Calls("Environment.GetEnvironmentVariable");
        Assert.True(
            reads.Count == 1,
            $"expected exactly one env-var read in TestSeam.Start, found {reads.Count}");

        // The name lives in one const; the gate reads that const, and the
        // const is the opt-in literal. Pinning both halves keeps a rename
        // of either side from splitting them apart.
        var envVar = source.Field("EnvVar").Variable;
        Assert.True(
            envVar.Initializer is not null
            && envVar.Initializer.Value.ToString().Contains(
                "WINTTY_TEST_SEAM", StringComparison.Ordinal),
            "the seam's env-var const no longer names WINTTY_TEST_SEAM");
        Assert.Equal("EnvVar", reads[0].ArgumentList.Arguments[0].ToString());

        // The polarity is the surface: "1" means on, anything else -- unset,
        // empty, "0", "true" -- is off. A comparison whose text no longer
        // demands the literal "1" has widened the gate.
        var guard = start.Body!.Statements
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax>()
            .FirstOrDefault();
        Assert.True(guard is not null, "TestSeam.Start has no gate guard");
        Assert.Contains(
            "\"1\"", guard!.Condition.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "!=", guard!.Condition.ToString(), StringComparison.Ordinal);

        // And the gate has to run before the server does: nothing may spawn
        // a pipe from Start ahead of it.
        var guardEnd = guard.Span.End;
        var server = start.Calls("ServeAsync");
        Assert.True(
            server.Count == 1 && server[0].Span.Start > guardEnd,
            "TestSeam.Start reaches the pipe server before the opt-in gate");
    }

    [Fact]
    public void ThePipeLifecycle_IsBoundedByTheWindow()
    {
        var source = ShellSource.Load("Testing.TestSeam.cs");

        // One server loop, and it is the only place a pipe exists.
        var serve = source.Method("ServeAsync");
        Assert.True(
            serve.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax
                .ObjectCreationExpressionSyntax>().Count(c =>
                    c.Type.ToString().Contains("NamedPipeServerStream")) == 1,
            "expected exactly one NamedPipeServerStream creation, in ServeAsync");
        Assert.True(
            serve.Calls("pipe.WaitForConnectionAsync").Count == 1,
            "the server loop no longer waits for exactly one connection per pipe");
        Assert.True(
            serve.Calls("pipe.Dispose").Count == 1,
            "the server no longer disposes the pipe between connections");

        // A name owned by another opted-in instance STOPS the server: one
        // seam per machine. The creation-failure catch must return -- a
        // continue here recreates the same refused pipe as fast as the
        // loop can spin -- and the connection-level catch must NOT return,
        // because a client hanging up is not the server's death. Both
        // catches declare Exception and discriminate in the filter.
        var catches = serve.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CatchClauseSyntax>()
            .Where(c => c.Filter is not null
                        && c.Filter!.FilterExpression.ToString().Contains("IOException"))
            .ToList();
        var creation = catches.Single(c => c.Filter!.FilterExpression.ToString()
            .Contains("UnauthorizedAccessException"));
        Assert.Contains(
            creation.Block.Statements,
            s => s is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax);
        var wait = serve.Calls("pipe.WaitForConnectionAsync").Single();
        Assert.True(
            creation.Span.End <= wait.Span.Start,
            "the name-taken refusal must guard the pipe creation, before the "
            + "connection wait it must never reach");
        var serving = catches.Single(c => c.Filter!.FilterExpression.ToString()
            .Contains("ObjectDisposedException"));
        Assert.DoesNotContain(
            serving.Block.Statements,
            s => s is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax);

        // Start subscribes the window's close to the server's cancellation,
        // so a closed window cannot leave a listening pipe behind.
        var start = source.Method("Start");
        Assert.True(
            start.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax
                .MemberAccessExpressionSyntax>().Count(m =>
                    m.Name.Identifier.ValueText == "Closed") == 1,
            "TestSeam.Start no longer subscribes exactly one window.Closed");
        Assert.True(
            start.Calls("Task.Run").Count == 1,
            "TestSeam.Start no longer runs the server as exactly one background task");

        // The marshal is the whole fidelity story: every command funnels
        // through the one dispatcher hop, and the drag handoff runs below
        // the drag tick's priority.
        var marshal = source.Method("RunOnUiThreadAsync");
        Assert.True(
            marshal.Calls("window.DispatcherQueue.TryEnqueue").Count == 1,
            "the UI marshal is no longer exactly one TryEnqueue");
        var execute = source.Method("ExecuteAsync");
        Assert.True(
            execute.Calls("RunOnUiThreadAsync").Count == 1,
            "commands no longer funnel through the single UI marshal");
    }

    /// <summary>
    /// The fuzz suite's gesture commands are only honest while they drive
    /// the strip's REAL pointer handlers: each op routes to its one strip
    /// walker, select goes through the manager's own activation, and the
    /// shared walk feeds DragMove under the seam's pointer id with the
    /// Low-priority handoff per tick -- never a second implementation of
    /// the grammar.
    /// </summary>
    [Fact]
    public void TheGestureOps_DriveTheStripsRealHandlers()
    {
        var seam = ShellSource.Load("Testing.TestSeam.cs");
        var dispatch = seam.Method("ExecuteOnUiThreadAsync");
        Assert.Single(dispatch.Calls("strip.TestSeamDragPacedAsync"));
        Assert.Single(dispatch.Calls("strip.TestSeamDragZoneAsync"));
        Assert.Single(dispatch.Calls("strip.TestSeamDragToHeaderAsync"));
        Assert.Single(dispatch.Calls("manager.Activate"));

        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        var walk = strip.Method("SeamWalkAsync");
        var move = walk.Calls("DragMove").Single();
        Assert.Contains("TestSeamPointerId", move.ToString());
        Assert.Single(walk.Calls("Testing.TestSeam.WaitForLowPriorityAsync"));
        // Wall-clock pacing is the walker's own optional tick, for the
        // filming driver; the settle handoff above stays unconditional.
        Assert.Contains("Task.Delay(tickDelayMs)", walk.ToString());
        foreach (var name in new[]
        {
            "TestSeamDragPacedAsync", "TestSeamDragZoneAsync", "TestSeamDragToHeaderAsync",
        })
        {
            var walker = strip.Method(name);
            Assert.Single(walker.Calls("DragPress"));
            Assert.Single(walker.Calls("DragRelease"));
        }
    }

    /// <summary>
    /// Every walker that must cross aims with the machine's own numbers,
    /// at every site: Evaluate's inequality is strict (center PLUS the
    /// token), so a walker aiming AT a slot center stalls one token short
    /// of its final commit -- the exact regression the base walker
    /// shipped. The base and paced walkers must overshoot past the center
    /// in the travel direction by TabStripMotion.CrossingHysteresisPx (a
    /// literal would fall silently behind a token change), the zone walk
    /// overshoots by the same token, and the header walk re-reads the
    /// header's live center every tick, because crossings churn the list
    /// under the walk.
    /// </summary>
    [Fact]
    public void TheBoundaryAndHeaderWalks_AimWithTheMachinesOwnNumbers()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        foreach (var name in new[]
        {
            "TestSeamDragAsync", "TestSeamDragPacedAsync", "TestSeamDragZoneAsync",
        })
        {
            Assert.Contains(
                "TabStripMotion.CrossingHysteresisPx",
                strip.Method(name).ToString());
        }
        // The slot walkers must aim PAST the center in the travel
        // direction, not merely mention the token somewhere.
        foreach (var name in new[] { "TestSeamDragAsync", "TestSeamDragPacedAsync" })
        {
            Assert.Contains("Math.Sign(to - from)", strip.Method(name).ToString());
        }

        var header = strip.Method("TestSeamDragToHeaderAsync");
        var headerWalk = header.Calls("SeamWalkAsync").Single();
        Assert.Contains("HeaderCenterY(group)", headerWalk.ToString());
    }

    /// <summary>
    /// The filming driver aligns frames to the paced walk's own clock, so
    /// the commit timestamp must come from the manager index moving --
    /// gesture truth, not a schedule -- and the drag response must carry
    /// it out, along with the release stamp.
    /// </summary>
    [Fact]
    public void ThePacedWalk_TimestampsTheCommit_AndTheResponseCarriesIt()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        var paced = strip.Method("TestSeamDragPacedAsync").ToString();
        Assert.Contains("outcome.ReleaseMs = clock.ElapsedMilliseconds", paced);

        // The commit stamp must be taken INSIDE the walk closure, where
        // it lands on the tick the manager index moved -- the earliest
        // honest reading. The post-walk fallback alone stamps LATE,
        // which SHRINKS the measured gap and breaks the oracle's
        // "a flattering gap is impossible" polarity.
        var walked = strip.Method("TestSeamDragPacedAsync").DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax>()
            .Single(f => f.Identifier.ValueText == "Walked").ToString();
        Assert.Contains("_manager.IndexOf(tab) != from", walked);
        Assert.Contains("outcome.CommitMs = clock.ElapsedMilliseconds", walked);

        var seam = ShellSource.Load("Testing.TestSeam.cs");
        var response = seam.Method("DragJson").ToString();
        Assert.Contains("\"commitMs\"", response);
        Assert.Contains("\"releaseMs\"", response);

        // And the state block names the active tab through the manager's
        // own index, for the guard scenario that asserts the fold moved
        // nothing.
        var state = seam.Method("WriteState");
        var active = state.Calls("manager.IndexOf").Single();
        Assert.Equal("manager.ActiveTab", active.ArgumentList.Arguments[0].ToString());
    }
}
