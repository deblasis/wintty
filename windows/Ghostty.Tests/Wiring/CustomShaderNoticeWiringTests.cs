using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The notice itself (its copy, and the latch that stops it repeating) is
/// unit-tested in CustomShaderNoticeSourceTests. What that cannot reach is
/// which surfaces are allowed to reach the notice at all, which lives in the
/// WinUI host's action switch.
///
/// A gallery preview's shader does not come from the user's custom-shader
/// setting, so a preview failure must neither show the notice (its copy
/// blames settings the user never touched) nor consume the source's latch
/// (which never re-arms for the same reason, and would swallow a later
/// genuine config-shader failure for the rest of the session).
/// </summary>
public class CustomShaderNoticeWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Hosting.GhosttyHost.cs");

    private static IfStatementSyntax PreviewGuard(SyntaxNode section) =>
        Assert.Single(
            section.DescendantNodes().OfType<IfStatementSyntax>(),
            s => s.Condition.ToString() == "control.IsPreviewSurface");

    [Fact]
    public void PreviewSurfaces_LeaveTheCustomShaderCaseImmediately()
    {
        var section = Host().Case("OnAction", "CustomShaderFailed");

        // Handled, not unhandled: returning 0 would let libghostty treat the
        // action as unconsumed.
        Assert.Equal("return 1;", PreviewGuard(section).Statement.ToString());
    }

    [Fact]
    public void PreviewGuard_RunsBeforeTheNoticeSourceIsConsulted()
    {
        var section = Host().Case("OnAction", "CustomShaderFailed");

        // Order is the whole fix. Resolve latches on the reason and never
        // re-arms, so a preview reaching it suppresses the next real one even
        // if the banner itself is skipped afterwards.
        Assert.True(
            PreviewGuard(section).SpanStart < section.Call("_customShaderNotices.Resolve").SpanStart,
            "the preview guard must precede _customShaderNotices.Resolve");
    }

    [Fact]
    public void ConfigShaderFailures_StillShowTheNotice()
    {
        var section = Host().Case("OnAction", "CustomShaderFailed");

        // The other half: gating must not have quietly removed the real path.
        Assert.Single(section.Calls("notifications.Show"));
        Assert.Equal("notice", section.Call("notifications.Show").Arg(0));
    }
}
