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
            serve.Calls("pipe?.Dispose").Count >= 1,
            "the server no longer disposes the pipe between connections");

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
}
