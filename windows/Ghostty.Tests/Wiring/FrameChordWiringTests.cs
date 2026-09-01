using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Guards for the frame chord router that cannot be written against
/// behaviour: the router reads live keyboard state and a live focus tree,
/// neither of which exists in a test host.
/// </summary>
public class FrameChordWiringTests
{
    /// <summary>
    /// Every modifier a chord can carry has to be READ, or the chord that
    /// arrives is not the chord the user pressed.
    ///
    /// This is the guard the matcher's own tests cannot be: FrameChordMatcher
    /// has always handled `win` correctly, so its tests stay green whether or
    /// not the flag is ever set. The defect lived one layer out -- the
    /// Windows key was never read, so Win+Ctrl+T reached the matcher looking
    /// exactly like Ctrl+T and opened a tab. There is no VK for "the Windows
    /// key", so both sides have to be named.
    /// </summary>
    [Fact]
    public void CurrentChordModifiers_ReadsEveryModifierIncludingBothWindowsKeys()
    {
        var method = ShellSource.Load("Controls.TerminalControl.xaml.cs")
            .Method("CurrentChordModifiers");

        var keysRead = method.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression.ToString().EndsWith("VirtualKey"))
            .Select(m => m.Name.Identifier.Text)
            .ToHashSet();

        Assert.Contains("Control", keysRead);
        Assert.Contains("Shift", keysRead);
        Assert.Contains("Menu", keysRead);
        Assert.Contains("LeftWindows", keysRead);
        Assert.Contains("RightWindows", keysRead);
    }

    /// <summary>
    /// ...and having read it, the method has to report it. A read whose
    /// result never reaches the returned set is the same bug with more code.
    /// </summary>
    [Fact]
    public void CurrentChordModifiers_ReportsTheWindowsModifier()
    {
        var method = ShellSource.Load("Controls.TerminalControl.xaml.cs")
            .Method("CurrentChordModifiers");

        var reported = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "mods")
            .Select(a => a.Right.ToString())
            .ToList();

        Assert.Contains(reported, r => r.EndsWith("VirtualKeyModifiers.Windows"));
    }

    /// <summary>
    /// The router's focus gate is an ALLOW-list, so a focus kind added later
    /// refuses until someone decides otherwise. Written as a deny-list it
    /// grew a hole every time a new place to put focus appeared -- which is
    /// how an open tab overview ended up counting as the frame, and Ctrl+W
    /// closed a tab whose tile was still on screen.
    /// </summary>
    [Fact]
    public void TheFocusGate_AllowsRatherThanDenies()
    {
        var method = ShellSource.Load("MainWindow.FrameChords.cs")
            .Method("TryDispatchFrameChord");

        var gate = method.DescendantNodes()
            .OfType<IsPatternExpressionSyntax>()
            .FirstOrDefault(p => p.Expression.ToString() == "CurrentFrameChordFocus()");

        Assert.NotNull(gate);
        // `is not (Frame or None)` -- a negated pattern over the states that
        // MAY claim, rather than a list of the states that may not.
        Assert.IsType<UnaryPatternSyntax>(gate!.Pattern);
        var allowed = gate.Pattern.ToString();
        Assert.Contains("Frame", allowed);
        Assert.Contains("None", allowed);
        Assert.DoesNotContain("Pane", allowed);
    }

    /// <summary>
    /// An overlay is answered as a question about state, not only about
    /// focus. An overview can be open holding no focus at all, and a gate
    /// that only read focus would see None -- which the router treats as
    /// claimable.
    /// </summary>
    [Fact]
    public void OverlayState_IsCheckedBeforeFocusIsRead()
    {
        var method = ShellSource.Load("MainWindow.FrameChords.cs")
            .Method("CurrentFrameChordFocus");

        var body = method.Body!.Statements;
        var overlayAt = body.ToList().FindIndex(s => s.ToString().Contains("AnyOverlayOpen"));
        var focusAt = body.ToList().FindIndex(s => s.ToString().Contains("GetFocusedElement"));

        Assert.True(overlayAt >= 0, "the overlay state must be consulted");
        Assert.True(focusAt >= 0, "focus must still be read");
        Assert.True(
            overlayAt < focusAt,
            "an open overlay must be refused before focus is read, or an overlay "
                + "holding no focus reports None and the router claims the key");
    }

    /// <summary>
    /// Every overlay the window can raise is named. A popup added later and
    /// not added here reopens the same hole, so the list is asserted against
    /// the popups the window actually declares.
    /// </summary>
    [Fact]
    public void AnyOverlayOpen_NamesEveryPopupTheWindowDeclares()
    {
        var consulted = ShellSource.Load("MainWindow.FrameChords.cs")
            .Method("AnyOverlayOpen")
            .ToString();

        foreach (var popup in new[]
                 {
                     "CommandPalettePopup",
                     "TabSwitcherPopupHost",
                     "TabOverviewHost",
                 })
        {
            Assert.Contains(popup, consulted);
        }
    }

    /// <summary>
    /// A KeyDown handler outlives the window: it can run after
    /// OnClosedAsync has torn the panes down. Gating on _isClosed rather
    /// than on AppWindow is the file's documented rule (issue 208) --
    /// AppWindow is still readable during closing.
    /// </summary>
    [Theory]
    [InlineData("OnFrameChordKeyDown")]
    [InlineData("TryDispatchFrameChord")]
    public void TheChordEntryPoints_GateOnIsClosed(string method)
    {
        var source = ShellSource.Load("MainWindow.FrameChords.cs").Method(method).ToString();

        Assert.Contains("_isClosed", source);
    }
}
