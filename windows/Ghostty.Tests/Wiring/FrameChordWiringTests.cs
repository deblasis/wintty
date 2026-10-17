using System;
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

    /// <summary>
    /// A chord pressed on a strip row names THAT row's tab. The router used
    /// to hand every matched action to the active surface: with focus on a
    /// row, close_tab closed a pane of whichever tab happened to be active
    /// behind the strip, and never raised the close confirmation the row's
    /// own close button would have.
    /// </summary>
    [Fact]
    public void TabRowChords_TargetTheRow_BeforeTheActiveLeaf()
    {
        var dispatch = ShellSource.Load("MainWindow.FrameChords.cs")
            .Method("TryDispatchFrameChord");

        // The row claim: an if that consults the focused strip row and
        // hands the action to the row-targeted dispatch.
        var claim = dispatch.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("TryExecuteTabRowAction")
                        && i.Condition.ToString().Contains("FocusedStripRowTab"))
            .ToList();
        Assert.True(
            claim.Count == 1,
            "the router must resolve a chord against the focused strip row "
                + "(FocusedStripRowTab + TryExecuteTabRowAction) before it falls "
                + "back to the active surface; found " + claim.Count + " such guard(s).");

        // ...and the claim holds the chord, or the key would reach the pane
        // path too and one press would fire twice.
        Assert.Contains(
            claim[0].DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression?.ToString() == "true");

        // The active surface stays the fallback, and only the fallback: the
        // row guard stands before it, so a row-targeted chord can never
        // reach the leaf.
        var fallback = Assert.Single(
            dispatch.Calls("TryExecuteBindingAction").ToList(), _ => true);
        Assert.True(
            claim[0].Span.Start < fallback.Span.Start,
            "the strip-row resolution must precede the active-surface fallback");

        // The row is asked of FOCUS, not of the active tab: the row a chord
        // was pressed on is the target, and it need not be the active tab.
        var focus = ShellSource.Load("MainWindow.FrameChords.cs")
            .Method("FocusedStripRowTab");
        Assert.Single(focus.Calls("FocusManager.GetFocusedElement").ToList(), _ => true);
        Assert.Single(focus.CallsEndingWith("TabFromElement").ToList(), _ => true);
        Assert.DoesNotContain(
            focus.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().Contains("ActiveTab"));
    }

    /// <summary>
    /// The row-targeted close rides the same entry the strip's own close
    /// button uses: RequestCloseTabAsync is where TabCloseConfirmation
    /// (confirm-close-surface) and the strip focus hand-back live, and a
    /// second close path would grow its own policy. And only close_tab:this
    /// may retarget: the sweep modes anchor on the active tab, not the row.
    /// </summary>
    [Fact]
    public void TheTabRowClose_RidesTheConfirmationPath_AndOnlyForThis()
    {
        var rowAction = ShellSource.Load("MainWindow.FrameChords.cs")
            .Method("TryExecuteTabRowAction");

        Assert.Single(rowAction.CallsEndingWith("RequestCloseTabAsync").ToList(), _ => true);

        // No second policy: no direct manager close, no surface write.
        Assert.Empty(rowAction.Calls("CloseTab"));
        Assert.Empty(rowAction.CallsEndingWith("SurfaceBindingAction").ToList());

        // Mode-exact: a prefix match would also catch close_tab:other and
        // close_tab:right, which sweep relative to the ACTIVE tab and have
        // no answer at a row.
        Assert.Contains(
            rowAction.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.ToString() == "\"close_tab:this\"");
        Assert.DoesNotContain(
            rowAction.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().EndsWith("StartsWith", StringComparison.Ordinal));
    }

    /// <summary>
    /// The router must not learn what a row looks like in either strip: the
    /// answer lives behind ITabHost, one member, implemented by both hosts,
    /// so a third layout cannot forget it.
    /// </summary>
    [Fact]
    public void BothHosts_AnswerTheFocusedRow_BehindTheSharedInterface()
    {
        var itab = ShellSource.Load("Tabs.ITabHost.cs").Root;
        Assert.Contains(
            itab.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.ValueText == "TabFromElement");

        var vertical = ShellSource.Load("Tabs.VerticalTabHost.xaml.cs")
            .Method("TabFromElement");
        Assert.Single(vertical.CallsEndingWith("TabFromElement").ToList(), _ => true);

        var horizontal = ShellSource.Load("Tabs.TabHost.xaml.cs")
            .Method("TabFromElement");
        Assert.Single(
            horizontal.DescendantNodes().OfType<GenericNameSyntax>()
                .Where(g => g.Identifier.ValueText == "FindAncestor")
                .Where(g => g.TypeArgumentList.ToString().Trim('<', '>') == "TabViewItem")
                .ToList(),
            _ => true);
    }
}
