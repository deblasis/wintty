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
    public void TheHealthCase_AppliesBothHalvesOfTheChange()
    {
        var section = Host().Case("OnAction", "RendererHealth");

        Assert.Single(section.Calls("_rendererHealthNotices.Update"));
        Assert.Equal("show", section.Call("notifications.Show").Arg(0));
        Assert.Equal("dismiss", section.Call("notifications.Dismiss").Arg(0));
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
    public void ClosingASurface_ForgetsItsHealth_AndAppliesTheDismissal()
    {
        var unregister = Host().Method("Unregister");

        Assert.Single(unregister.Calls("_rendererHealthNotices.Forget"));

        // The dismissal half matters as much as the Forget: without it the
        // banner survives the close of the only pane that was ever broken,
        // and nothing left alive can clear it.
        Assert.Single(unregister.Calls("notifications.Dismiss"));
        Assert.Equal("dismiss", unregister.Call("notifications.Dismiss").Arg(0));
    }

    [Fact]
    public void BothHealthPaths_GoThroughTheDispatcher()
    {
        // Two reasons, and the second is the one that bites. The source is
        // documented UI-thread-only and shared statically across windows, so
        // hoisting either call out of a lambda is a silent data race. And they
        // must share the queue: OnAction decodes off-thread and enqueues its
        // Update, so a synchronous Forget racing a device loss runs first,
        // finds nothing, and lets the Update strand a dead surface in the set.
        var host = Host();

        foreach (var (method, call) in new[]
                 {
                     ("OnAction", "_rendererHealthNotices.Update"),
                     ("Unregister", "_rendererHealthNotices.Forget"),
                 })
        {
            var enqueued = host.Method(method)
                .Calls("_dispatcher.TryEnqueue")
                .Any(e => e.Calls(call).Count == 1);

            Assert.True(enqueued, $"{call} must sit inside a _dispatcher.TryEnqueue in {method}");
        }
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
