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

    [Fact]
    public void OnLaunchedSeedsTheHangWatchdogDumpModeOnceConfigExists()
    {
        var method = App().Method("OnLaunched");
        var config = method.Body!.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is AssignmentExpressionSyntax a &&
                         a.Right is ObjectCreationExpressionSyntax c &&
                         c.Type.ToString() == "ConfigService");
        var seed = method.Body.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "Diagnostics.HangWatchdog.ConfigureDumpMode");

        // The watchdog arms before the config service can exist, so the
        // hang-dump scope reaches it only through this seed. It has to
        // follow the construction, and a stall in the window before it
        // captures the triage default, which is the safe direction.
        Assert.True(config.Index < seed.Index,
            "the hang dump mode must be seeded after the config service is constructed");
    }

    [Fact]
    public void OnLaunchedShowsTheHangNoticeAfterTheNotificationServiceExists()
    {
        var method = App().Method("OnLaunched");
        var service = method.Body!.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is AssignmentExpressionSyntax a &&
                         a.Right is ObjectCreationExpressionSyntax c &&
                         c.Type.ToString() == "Ghostty.Core.Notifications.NotificationService");
        var evaluate = method.Body.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "ShowPreviousSessionHangNotice");

        // The evaluation's only output is a notice on the service; run
        // before it exists, the evidence would be read and the marker
        // written while the notice itself is dropped on the floor.
        Assert.True(service.Index < evaluate.Index,
            "the hang-evidence evaluation must run after the notification service is constructed");
    }

    [Fact]
    public void OnLaunchedShowsTheHangNoticeAfterTheSingleInstanceGate()
    {
        var method = App().Method("OnLaunched");
        var gate = method.Body!.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "HandleSingleInstanceGate");
        var evaluate = method.Body.Statements
            .Select((s, i) => (Statement: s, Index: i))
            .Single(t => t.Statement is ExpressionStatementSyntax e &&
                         e.Expression is InvocationExpressionSyntax i &&
                         i.CalleeText() == "ShowPreviousSessionHangNotice");

        // A secondary instance forwards and exits inside the gate. With
        // the evaluation before it, the secondary would advance the
        // last-launch marker and queue the notice into a service no
        // host ever binds: exactly when the user re-launched because
        // the primary hung, the one-per-stall notice is consumed
        // unseen.
        Assert.True(gate.Index < evaluate.Index,
            "the hang-evidence evaluation must run after the single-instance gate");
    }

    [Fact]
    public void HangWatchdogMinidumpFlagValuesMatchTheSdk()
    {
        // The dump masks are composed from raw MINIDUMP_TYPE literals,
        // and no behavioral test can see a wrong value: 0x40000 is
        // MiniDumpWithTokenInformation, not indirectly-referenced
        // memory (0x40), and a dump captured with that mask passes every
        // test while containing the wrong evidence, token data
        // included. Pin the literals to minidumpapiset.h so a typo is a
        // red test, not a shipped mask.
        var src = ShellSource.Load("Ghostty.Diagnostics.HangWatchdog.cs");
        Assert.Equal("0x2", src.Field("MiniDumpWithFullMemory").Variable.Initializer!.Value.ToString());
        Assert.Equal("0x4", src.Field("MiniDumpWithHandleData").Variable.Initializer!.Value.ToString());
        Assert.Equal("0x10", src.Field("MiniDumpScanMemory").Variable.Initializer!.Value.ToString());
        Assert.Equal("0x20", src.Field("MiniDumpWithUnloadedModules").Variable.Initializer!.Value.ToString());
        Assert.Equal("0x800", src.Field("MiniDumpWithFullMemoryInfo").Variable.Initializer!.Value.ToString());
        Assert.Equal("0x1000", src.Field("MiniDumpWithThreadInfo").Variable.Initializer!.Value.ToString());
        Assert.Equal("0x40", src.Field("MiniDumpWithIndirectlyReferencedMemory").Variable.Initializer!.Value.ToString());

        // The masks' compositions too: the whole secrets posture of the
        // default hangs on TriageDumpFlags not containing
        // MiniDumpWithFullMemory, and no behavioral test can see a flag
        // quietly dropped or added.
        var triage = src.Field("TriageDumpFlags").Variable.Initializer!.Value.ToString();
        Assert.Contains("MiniDumpWithHandleData", triage);
        Assert.Contains("MiniDumpScanMemory", triage);
        Assert.Contains("MiniDumpWithUnloadedModules", triage);
        Assert.Contains("MiniDumpWithThreadInfo", triage);
        Assert.Contains("MiniDumpWithIndirectlyReferencedMemory", triage);
        Assert.DoesNotContain("MiniDumpWithFullMemory", triage);

        var full = src.Field("FullDumpFlags").Variable.Initializer!.Value.ToString();
        Assert.Contains("MiniDumpWithFullMemory", full);
        Assert.Contains("MiniDumpWithHandleData", full);
        Assert.Contains("MiniDumpWithFullMemoryInfo", full);
        Assert.Contains("MiniDumpWithThreadInfo", full);
    }
}

file static class DiagnosticsSyntaxQueries
{
    public static System.Collections.Generic.IEnumerable<ConstructorDeclarationSyntax> Constructors(
        this ShellSource source) =>
        source.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>();
}
