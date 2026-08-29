using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A forwarded launch that arrives before the app can act on it now waits
/// instead of vanishing, and the pre-XAML splash is skippable when a warm
/// session is ready to attach. Both are timing, and timing is exactly what a
/// test host cannot see, so these guards pin the shape: one readiness edge,
/// reached after the server is listening, and a splash gated on a decision
/// that can say no.
///
/// What they can catch: the drain deleted, or moved ahead of the server so a
/// request still in flight is replayed into an app that has already begun
/// coming down, or a splash shown without consulting the gate.
///
/// What they cannot catch: whether a deferred launch ever opens a window.
/// That needs a second process forwarding over a real pipe.
/// </summary>
public class LaunchDeferralWiringTests
{
    private static ShellSource OnLaunched() => ShellSource.Load("App.xaml.cs").Method("OnLaunched");

    /// <summary>
    /// There has to be exactly one readiness edge, and it has to come after
    /// the server is listening. Before the server starts there is nothing to
    /// defer; after it, a request can be enqueued from a pipe thread while the
    /// UI thread is still inside OnLaunched, and replaying the queue before
    /// that point would take a launch that arrived legally and act on it in
    /// whatever half-built state the app was in when the queue was drained.
    /// </summary>
    [Fact]
    public void TheQueueIsDrained_AfterTheForwardingServerStarts()
    {
        var launched = OnLaunched();

        var statements = launched.Body!.Statements;
        var server = statements.TakeWhile(
            s => !s.ToString().Contains("StartSingleInstanceServer()")).Count();
        var drain = statements.TakeWhile(
            s => !s.ToString().Contains("DrainDeferredLaunches()")).Count();

        Assert.True(server < statements.Count, "OnLaunched no longer starts the forwarding server");
        Assert.True(drain < statements.Count, "OnLaunched no longer drains the deferral queue");
        Assert.True(
            server < drain,
            "the deferral queue is drained before the server starts, which replays "
            + "launches that had not arrived yet");
    }

    /// <summary>
    /// The drain is the readiness edge, so it must be reachable exactly once.
    /// A second one means two places can latch readiness, and the queue's
    /// contract is that the second call drains nothing -- a caller relying on
    /// it to replay would silently drop whatever arrived in between.
    /// </summary>
    [Fact]
    public void ThereIsExactlyOneDrainCall()
    {
        var app = ShellSource.Load("App.xaml.cs");

        Assert.Single(app.Root.Calls("DrainDeferredLaunches"));
    }

    /// <summary>
    /// The not-ready path has to end in the queue. Rewriting it back to a bare
    /// return compiles, passes every unit test, and puts the silent launch
    /// loss straight back: the secondary has already exited believing it was
    /// served.
    /// </summary>
    [Fact]
    public void TheNotReadyPathDefersRatherThanReturningBare()
    {
        var method = ShellSource.Load("App.xaml.cs").Method("OpenWindowFromLaunch");

        Assert.NotNull(method.Call("_deferredLaunches.Defer"));
    }

    /// <summary>
    /// The splash decision is made before WinUI starts and is the one place
    /// that can say no. Gating on anything else, or inlining the role test,
    /// leaves a warm session with a full-size topmost rectangle drawn over it
    /// on its way up.
    /// </summary>
    [Fact]
    public void TheSplashIsShownOnlyThroughTheGate()
    {
        var gui = ShellSource.Load("Program.cs").Method("StartGui");

        var show = Assert.Single(gui.Calls("Ghostty.Shell.SplashWindow.Show"));

        // The gate is a property read, so it parses as a member access rather
        // than an invocation and has to be found through the if that guards
        // the show. Reading the guard rather than the proximity of two spans
        // is what stops a second unconditional Show from passing.
        var gate = show.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.True(
            gate?.Condition.ToString().Contains("ShouldShowLaunchSplash") == true,
            "SplashWindow.Show is not guarded by the launch-splash decision");
    }
}
