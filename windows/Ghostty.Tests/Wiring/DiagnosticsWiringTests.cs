using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The diagnostics' whole value is that they are running when the bad
/// thing happens: a watchdog that never started and a stderr capture
/// installed after libghostty booted record exactly nothing. These pins
/// hold the wiring points -- the calls in App's constructor/OnLaunched
/// and, for the capture, the ordering constraint that it runs before
/// any native initialization.
/// </summary>
public class DiagnosticsWiringTests
{
    private static ShellSource App() => ShellSource.Load("Ghostty.App.xaml.cs");

    [Fact]
    public void AppInstallsTheStderrCaptureBeforeUnhandledHandlers()
    {
        var ctor = App().Constructors().Single(c =>
            c.Initializer == null && c.Body is not null &&
            c.Body.Statements.OfType<ExpressionStatementSyntax>()
                .Any(s => s.Expression is InvocationExpressionSyntax i &&
                          i.CalleeText() == "Diagnostics.NativeStderrCapture.Install"));

        // The capture must precede InitializeComponent: libghostty writes
        // to stderr during early boot (the log installer runs later),
        // so a capture that lands after the native side initializes has
        // already missed whatever it was installed to catch.
        var install = ctor.Body!.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "Diagnostics.NativeStderrCapture.Install");
        var initComponent = ctor.Body.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "InitializeComponent");
        Assert.True(install.Index < initComponent.Index,
            "the stderr capture must be installed before InitializeComponent");
    }

    [Fact]
    public void OnLaunchedArmsTheHangWatchdogFirst()
    {
        var method = App().Method("OnLaunched");
        var arm = method.Body!.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "Diagnostics.HangWatchdog.Start");

        // First statement of the launch: the #1036 class of hang existed
        // from the first frame, and a watchdog armed after a crashing
        // early step would not have started at all.
        Assert.Equal(0, arm.Index);
    }
}

file static class DiagnosticsSyntaxQueries
{
    public static System.Collections.Generic.IEnumerable<ConstructorDeclarationSyntax> Constructors(
        this ShellSource source) =>
        source.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>();
}
