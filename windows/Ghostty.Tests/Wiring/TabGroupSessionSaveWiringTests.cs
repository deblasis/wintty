using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The SAVE half of the group session. The restore side runs on managers
/// that unit tests can drive (TabSessionPinGroupTests), but the save side
/// is MainWindow.CaptureSession, which only exists where WinUI does --
/// so the wiring facts live here, against the parsed source. The
/// load-bearing wiring is two-fold: every captured tab carries its pin
/// flag and its group id (a capture that drops the id dissolves the
/// group on the next launch), and the window's group registry is
/// captured beside the tabs, because membership alone cannot restore a
/// group's identity, title, color, or collapse bit.
/// </summary>
public class TabGroupSessionSaveWiringTests
{
    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    [Fact]
    public void TheSavePath_carries_pin_group_and_the_registry()
    {
        var capture = MainWindow().Method("CaptureSession");

        // Every tab's capture names its pin flag and its group id, in
        // that order, as the capture op's trailing arguments. Exact texts:
        // a negated flag or a renamed receiver would still "mention" the
        // field and pass a substring check while saving the wrong state.
        var captureTab = capture.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText().EndsWith("SessionCapture.CaptureTab",
                StringComparison.Ordinal));
        Assert.Equal("tab.IsPinned", captureTab.Arg(5));
        Assert.Equal("tab.Group?.Id", captureTab.Arg(6));

        // And the registry rides with them: membership (GroupId) cannot
        // restore a group on its own -- the Groups list is where id,
        // title, color, and the shared collapse bit live.
        var addGroups = capture.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "win.Groups.AddRange").ToList();
        Assert.Single(addGroups);
        var registryArg = Assert.IsType<InvocationExpressionSyntax>(
            addGroups[0].ArgumentList.Arguments[0].Expression);
        Assert.True(
            registryArg.CalleeText().EndsWith("SessionCapture.CaptureGroups",
                StringComparison.Ordinal),
            "the registry must be captured through SessionCapture.CaptureGroups");
        Assert.Equal("_tabManager.Groups", registryArg.Arg(0));
    }
}
