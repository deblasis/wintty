using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Closing a tab from a strip must leave the keyboard somewhere useful.
///
/// The close affordances are the only controls in a tab strip a pointer
/// lands on without activating the tab. On the vertical strip that made
/// the X itself a focusable Button: clicking the X of a BACKGROUND tab
/// moved keyboard focus onto it, the close left the removed row (and its
/// focused button) in the tree's place, and nothing reachable held focus
/// afterwards. TabManager raises ActiveTabChanged only when the ACTIVE tab
/// goes away, so the one hand-back the window owns -- FocusActiveLeaf in
/// that handler -- never ran, and the strip's own _refocusTab save tested
/// the ROW's FocusState, which reads Unfocused while its child holds the
/// focus.
///
/// Keybindings reach the app only through a focused TerminalControl, so
/// from that moment every chord went nowhere until the user clicked a
/// pane. A declined confirmation strands focus the same way, which is why
/// the hand-back cannot live only on the removal path.
/// </summary>
public class StripCloseFocusWiringTests
{
    [Fact]
    public void TheRowCloseButton_CannotTakeFocus()
    {
        var row = ShellSource.Load("Tabs.VerticalTabNavRow.cs");

        // The row's close button is built here, not in a template: the row
        // is a Grid whose Content the strip hands to a NavigationViewItem.
        var button = Assert.Single(
            row.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(o => o.Type.ToString() == "Button")
                .ToList(),
            _ => true);

        // Both halves, because they are two different switches:
        // IsTabStop only removes the control from the tab ORDER, while a
        // pointer click still moves focus onto it. A close button reachable
        // by a click is the whole repro, so AllowFocusOnInteraction is the
        // load-bearing one and IsTabStop keeps it out of Tab order besides.
        Assert.True(
            Initializer(button, "IsTabStop") == "false",
            "the row's close button must not be a tab stop, or Tab walks onto a "
                + "button that a close can delete out from under the focus; found "
                + $"'{Initializer(button, "IsTabStop")}'.");
        Assert.True(
            Initializer(button, "AllowFocusOnInteraction") == "false",
            "the row's close button must refuse focus from interaction: clicking a "
                + "BACKGROUND tab's X moved keyboard focus onto this button, and the "
                + "close then removed it with the row, leaving nothing focused; "
                + $"found '{Initializer(button, "AllowFocusOnInteraction")}'.");
    }

    [Fact]
    public void EveryStripClose_HandsKeyboardFocusBack_DeclinedConfirmationIncluded()
    {
        // Both hosts, and the horizontal one too: the hand-back is a
        // property of "a close was requested from a strip", not of the
        // strip that happens to be on screen.
        foreach (var (tail, host) in new[]
                 {
                     ("Tabs.TabHost.xaml.cs", "TabHost"),
                     ("Tabs.VerticalTabHost.xaml.cs", "VerticalTabHost"),
                 })
        {
            var src = ShellSource.Load(tail);
            var close = src.Method("RequestCloseTabAsync");

            // In a finally, and the finally is not decoration: the declined
            // confirmation removes nothing, so a hand-back hung on the
            // removal is exactly the case that stays broken.
            var guard = Assert.Single(
                close.DescendantNodes().OfType<TryStatementSyntax>().ToList(),
                _ => true);
            Assert.Single(
                guard.Block.Calls("TabCloseConfirmation.RequestAsync").ToList(),
                _ => true);
            Assert.NotNull(guard.Finally);

            // The hand-back answers only when focus is still in THIS strip:
            // it is the strip that cannot type, and a close driven by a
            // chord from the terminal must not yank focus out of a live one.
            var focusCall = Assert.Single(
                guard.Finally!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(i => i.CalleeText() == "FocusReturn?.Invoke")
                    .ToList(),
                _ => true);
            var predicate = Assert.IsType<IfStatementSyntax>(focusCall.Ancestors()
                .OfType<IfStatementSyntax>().First());
            Assert.Equal("FocusIsInStrip()", predicate.Condition.ToString());

            // And the predicate is this host's own subtree, not "somewhere
            // in the window": a TabHost that answered for the vertical
            // strip's row would report no focus where there is some, and
            // the hand-back would never fire.
            var probe = src.Method("FocusIsInStrip");
            Assert.Single(probe.Calls("FocusManager.GetFocusedElement").ToList(), _ => true);
            var ancestor = Assert.Single(
                probe.DescendantNodes().OfType<GenericNameSyntax>()
                    .Where(g => g.Identifier.ValueText == "FindAncestor")
                    .ToList(),
                _ => true);
            Assert.True(
                ancestor.TypeArgumentList.ToString().Trim('<', '>') == host,
                $"the strip predicate must walk up to {host}, the host it belongs "
                    + $"to; found '{ancestor.Parent}'.");
        }

        // The window owns the hand-back: the terminal lives there, so the
        // strips are handed the same delegate rather than each reaching for
        // a terminal of its own.
        var win = ShellSource.Load("MainWindow.xaml.cs");
        foreach (var field in new[]
                 {
                     "_horizontalTabHost.FocusReturn",
                     "_verticalTabHost.FocusReturn",
                 })
        {
            var assign = win.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == field)
                .ToList();
            Assert.True(
                assign.Count == 1,
                $"{field} must be assigned once, by the window that owns the "
                    + $"terminal the hand-back names; found {assign.Count}.");
            Assert.True(
                assign[0].Right.ToString() == "RefocusTerminalAfterStripRemoval",
                $"{field} must be the window's one refocus helper, so the two "
                    + $"strips cannot drift into two different answers; found "
                    + $"'{assign[0].Right}'.");
        }
    }

    [Fact]
    public void AnyTabRemoval_HandsKeyboardFocusBack()
    {
        var win = ShellSource.Load("MainWindow.xaml.cs");

        // Not only closes that came through RequestCloseTabAsync: the
        // chord, the context menu, close-group and reopen-closed-tab all
        // remove a tab without asking a strip, and the row that held focus
        // (when there was one) is gone just the same. The handler is the
        // one that unwires the tab (ApplyQuickTerminalBehaviour has a second
        // TabRemoved subscription, and it has no teardown to pair with).
        var handlers = win.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Left.ToString() == "_tabManager.TabRemoved")
            .Where(a => a.Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText() == "RemovePaneHost"))
            .ToList();
        Assert.True(
            handlers.Count == 1,
            "the TabRemoved handler that tears the tab down must be identifiable; "
                + $"found {handlers.Count}.");
        var hook = handlers[0];
        Assert.True(
            hook.Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText() == "RefocusTerminalAfterStripRemoval"),
            "TabRemoved is the one signal every close path raises; without the "
                + "hand-back there, a background close driven by a chord still "
                + "strands the keyboard.");

        // The helper itself: a live terminal needs nothing, anything else
        // does. Focus inside a PaneHost IS the terminal, so the predicate
        // asks about the pane tree rather than about the strip -- focus on
        // a dead row, on a close button, or on nothing at all all fail it,
        // and all three are the terminal-availability failure.
        var refocus = win.Method("RefocusTerminalAfterStripRemoval");
        Assert.True(
            refocus.DescendantNodes().OfType<GenericNameSyntax>()
                .Any(g => g.Identifier.ValueText == "FindAncestor"
                         && g.TypeArgumentList.ToString().Trim('<', '>') == "PaneHost"),
            "the hand-back must recognise a live terminal by asking about the "
                + "pane tree; a predicate about the strip cannot tell focus on a "
                + "dead row from focus on a running pane.");
        Assert.Single(refocus.Calls("FocusActiveLeaf").ToList(), _ => true);

        // Teardown is not a focus event: enqueuing a focus on a window on
        // its way out buys nothing and can land on a torn-down tree.
        Assert.True(
            refocus.DescendantNodes().OfType<IfStatementSyntax>()
                .Any(i => i.Condition.ToString() == "_isClosed"),
            "RefocusTerminalAfterStripRemoval must refuse once the window is "
                + "closed.");
    }

    private static string? Initializer(ObjectCreationExpressionSyntax creation, string property)
        => creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == property)
            .Select(a => a.Right.ToString())
            .SingleOrDefault();
}