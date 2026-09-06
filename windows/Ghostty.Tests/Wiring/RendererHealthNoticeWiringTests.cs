using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The banner's bookkeeping is unit-tested in RendererHealthNoticeSourceTests.
/// What that cannot reach is whether the WinUI host actually asks it anything,
/// which lives in the action switch and in surface teardown.
///
/// Both halves are load-bearing in a way a compiler cannot see. A handler that
/// shows and never dismisses leaves a "graphics device lost" banner on screen
/// after the device came back; a teardown that never forgets leaves the same
/// banner up after the only broken pane has been closed, with nothing left
/// that could ever clear it.
/// </summary>
public class RendererHealthNoticeWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Hosting.GhosttyHost.cs");

    [Fact]
    public void EveryHealthChange_GoesThroughTheOneApplier()
    {
        // All three producers hand their change to ApplyRendererHealth rather
        // than unpacking it themselves. That is the whole defence against a
        // call site applying only the Dismiss half: a change can carry both
        // when one banner replaces another, and dropping the Show there takes
        // the banner away from panes that are still broken.
        var host = Host();

        Assert.Single(host.Case("OnAction", "RendererHealth").Calls("ApplyRendererHealth"));
        Assert.Single(host.Method("Unregister").Calls("ApplyRendererHealth"));
        Assert.Single(host.Method("ForgetRendererHealth").Calls("ApplyRendererHealth"));

        // And none of them reaches past it to the service directly. Scoped to
        // the health case rather than the whole of OnAction, which also holds
        // the unrelated custom-shader notice.
        foreach (var scope in new SyntaxNode[]
                 {
                     host.Case("OnAction", "RendererHealth"),
                     host.Method("Unregister"),
                     host.Method("ForgetRendererHealth"),
                 })
        {
            Assert.Empty(scope.Calls("notifications.Show"));
            Assert.Empty(scope.Calls("notifications.Dismiss"));
        }
    }

    [Fact]
    public void TheApplier_DismissesBeforeItShows()
    {
        var apply = Host().Method("ApplyRendererHealth");

        Assert.Equal("show", apply.Call("notifications.Show").Arg(0));
        Assert.Equal("dismiss", apply.Call("notifications.Dismiss").Arg(0));

        // A pane being given up on replaces the rebuilding banner with the
        // abandoned one, and both carry the same DedupKey -- so showing first
        // makes the replacement a no-op and leaves the user reading advice
        // that no longer applies.
        Assert.True(
            apply.Call("notifications.Dismiss").SpanStart
                < apply.Call("notifications.Show").SpanStart,
            "notifications.Dismiss must precede notifications.Show; they share a DedupKey");
    }

    [Fact]
    public void PreviewSurfaces_LeaveTheHealthCaseImmediately()
    {
        var section = Host().Case("OnAction", "RendererHealth");

        // A gallery preview owns a separate device. Its loss says nothing about
        // the terminal the user is working in, and the copy would send them
        // looking in the wrong place. Handled, not unhandled: returning 0 would
        // let libghostty treat the action as unconsumed.
        var guard = Assert.Single(
            section.DescendantNodes().OfType<IfStatementSyntax>(),
            s => s.Condition.ToString() == "control.IsPreviewSurface");
        Assert.Equal("return 1;", guard.Statement.ToString());
        Assert.True(
            guard.SpanStart < section.Call("_rendererHealthNotices.Update").SpanStart,
            "the preview guard must precede _rendererHealthNotices.Update");
    }

    [Fact]
    public void ClosingASurface_ForgetsItsHealth()
    {
        Assert.Single(Host().Method("Unregister").Calls("_rendererHealthNotices.Forget"));
    }

    [Fact]
    public void EveryHealthPath_GoesThroughTheDispatcher()
    {
        // Two reasons, and the second is the one that bites. The source is
        // documented UI-thread-only and shared statically across windows, so
        // hoisting any of these out of a lambda is a silent data race. And
        // they must share one queue: OnAction decodes off-thread and enqueues
        // its Update, so a Forget that runs synchronously races ahead of it,
        // finds nothing, and lets the Update strand a surface in the set that
        // is gone or off screen and will never report again.
        var host = Host();

        foreach (var (method, call) in new[]
                 {
                     ("OnAction", "_rendererHealthNotices.Update"),
                     ("Unregister", "_rendererHealthNotices.Forget"),
                     ("ForgetRendererHealth", "_rendererHealthNotices.Forget"),
                 })
        {
            var enqueued = host.Method(method)
                .Calls("_dispatcher.TryEnqueue")
                .Any(e => e.Calls(call).Count == 1);

            Assert.True(enqueued, $"{call} must sit inside a _dispatcher.TryEnqueue in {method}");
        }
    }

    [Fact]
    public void HidingAPane_ForgetsItsHealth()
    {
        // A hidden pane cannot recover -- recovery runs from a draw and a
        // hidden surface does not draw -- so leaving it counted holds the
        // banner up over a window where everything visible is fine.
        var hide = ShellSource.Load("Panes.PaneHost.cs").Method("SetSurfaceVisibility");

        var call = Assert.Single(hide.Calls("_host.ForgetRendererHealth"));
        Assert.Equal("handle", call.Arg(0));

        // Only on the way out. Forgetting a pane that is becoming visible
        // would drop the banner for a pane the user is about to look at.
        var guard = Assert.Single(
            hide.DescendantNodes().OfType<IfStatementSyntax>(),
            s => s.Condition.ToString() == "!visible");
        Assert.Contains("ForgetRendererHealth", guard.Statement.ToString());
    }

    [Fact]
    public void MovingASurfaceBetweenWindows_KeepsItsHealth()
    {
        // Detach is the other half of a cross-window pane move, not a teardown:
        // the surface and its renderer live on under a different host. Forgetting
        // there would clear the banner for a pane that is still dark, and no
        // later report would bring it back -- the renderer only raises the
        // action on a change, and from its side nothing changed.
        Assert.Empty(Host().Method("Detach").Calls("_rendererHealthNotices.Forget"));
    }
}
