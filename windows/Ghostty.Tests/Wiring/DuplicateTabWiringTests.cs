using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The decided behavior ("same shell, same folder, per pane") is
/// three seams that must all still point at each other: the menu asks
/// for a duplicate of THIS tab (it used to call manager.NewTab(), which
/// copied nothing), the host consumes the shell's pwd report instead of
/// dropping it, and the clone is placed with the pin flag set before the
/// move (the flag defines the zone Move clamps into). Behaviour facts
/// live in SessionTreeTests and SessionProfileResolverTests; these are
/// the wiring guards for the hops a unit test cannot see.
/// </summary>
public class DuplicateTabWiringTests
{
    [Fact]
    public void TheDuplicateItem_ClonesThisTab_NotANewEmptyOne()
    {
        var build = ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Method("Build");

        // The old shape: a menu item that opened an empty tab and called
        // it a duplicate. Its return to Build must stay a failure.
        Assert.Empty(build.Calls("manager.NewTab"));

        Assert.Equal(
            "tab",
            build.Call("requestDuplicate").Arg(0));
    }

    [Fact]
    public void ThePwdAction_IsConsumed_AndReachesTheControl()
    {
        var section = ShellSource.Load("Hosting.GhosttyHost.cs")
            .Case("OnAction", "Pwd");

        // The marshaled report goes to the surface's own control; a null
        // or a different expression would record nothing or record the
        // wrong pane's directory.
        Assert.Equal(
            "pwd",
            section.Call("c.RaisePwdChanged").Arg(0));

        // Handled: returning 0 tells libghostty the apprt did not act.
        Assert.Contains("return 1;", section.Statements.ToString());
    }

    [Fact]
    public void TheControlSPwd_LandsOnThePane()
    {
        var handler = ShellSource.Load("Panes.PaneHost.cs")
            .Method("OnTerminalPwdChanged");

        var writes = handler.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "leaf.LastCwd")
            .ToList();
        Assert.True(
            writes.Count == 1 && writes[0].Right.ToString() == "pwd",
            "the pane's last-reported directory must be set from the report");
    }

    [Fact]
    public void Duplicate_CapturesRebuildsAndPlaces_PinBeforeMove()
    {
        var dup = ShellSource.Load("MainWindow.xaml.cs").Method("DuplicateTab");

        // The clone is the capture/restore pair, in that order.
        var capture = dup.Call("Ghostty.Core.Session.SessionCapture.CaptureTab");
        var adopt = dup.Call("_tabManager.AdoptTab");
        Assert.True(
            capture.SpanStart < adopt.SpanStart,
            "the live tab must be captured before the clone is adopted");

        // And the duplicate must never degenerate into a plain new tab.
        Assert.Empty(dup.Calls("_tabManager.NewTab"));

        // Pin comes home before the move: the flag defines the zone Move
        // clamps into, so moving first lands a pinned source's clone in
        // the wrong zone and the pin then relocates it away from its
        // source.
        var pinned = dup.Call("_tabManager.SetPinned");
        var moved = dup.Call("_tabManager.Move");
        Assert.True(
            pinned.SpanStart < moved.SpanStart,
            "the clone's pin flag must be set before the placement move");
    }
}
