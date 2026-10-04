using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The session-end half of session persistence, read off the parsed
/// source because the WinUI half cannot be loaded into a test host.
///
/// The defect: Windows ends a session by sending WM_QUERYENDSESSION and
/// WM_ENDSESSION and then killing the process. It never sends WM_CLOSE, so
/// <c>Window.Closed</c> never fired, the clean flag was never set, and
/// <c>window-save-state=default</c> had nothing to restore on the next
/// launch after a sign-out, a reboot or a Windows Update restart.
///
/// The message policy itself is tested where it can run, by faking the
/// messages: <c>SessionEndCaptureTests</c> in Ghostty.Tests.Windows drives
/// the same source the app builds. What is left here is "the shell wires
/// the hook to a clean write".
/// </summary>
public class SessionEndHookWiringTests
{
    private static ShellSource SessionManager() => ShellSource.Load("Session.SessionManager.cs");
    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// The hook is installed, it routes to the manager's clean capture, and
    /// that capture stops the debounce and asks for a CLEAN write. Any one
    /// of the three missing turns the reboot case back into "one fresh
    /// window".
    /// </summary>
    [Fact]
    public void The_session_end_hook_saves_the_session_clean()
    {
        var mainWindow = MainWindow();
        var ctors = mainWindow.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .ToList();

        Assert.True(
            ctors.Any(c => c.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Any(o => o.Type.ToString().EndsWith("SessionEndCapture"))),
            "MainWindow must install the session-end window-proc subclass");
        Assert.True(
            mainWindow.Method("OnClosedAsync").DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText() == "_sessionEndCapture?.Dispose"),
            "the subclass must be removed before the HWND is destroyed");

        // The action the hook runs is the manager's clean capture, not a
        // persist request (which is debounced, and the debounce never
        // fires once the session is over).
        Assert.Contains(
            "CaptureForSessionEnd",
            string.Concat(ctors.Select(c => c.ToString())));

        var sessionEnd = SessionManager().Method("CaptureForSessionEnd");
        var stop = sessionEnd.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "_debounce?.Stop");
        var save = sessionEnd.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText().EndsWith("SaveLiveWindows"));
        Assert.True(
            stop.SpanStart < save.SpanStart,
            "the debounce must be stopped first: a tick already queued would "
                + "rewrite CleanShutdown=false over the clean write");

        Assert.Equal("cleanShutdown: true", save.ArgumentList.Arguments[0].ToString());

        // And the mid-session write is the other half of the same call, so
        // "clean" is a parameter of one code path rather than a second
        // write that could drift from it.
        Assert.Equal(
            "cleanShutdown: false",
            SessionManager().Method("PersistLiveWindows")
                .DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.CalleeText().EndsWith("SaveLiveWindows"))
                .ArgumentList.Arguments[0].ToString());
    }
}