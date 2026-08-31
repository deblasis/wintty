using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The vertical strip's selected-row fill is a Border on its own Canvas,
/// positioned from the active NavigationViewItem's TransformToVisual. That
/// makes the fill's position a cached number rather than a property of the
/// row, and the cache is only as fresh as the layout it was read from.
///
/// Closing a tab above the active one moves the active row without changing
/// any size the strip subscribes to, and MUXC has not re-arranged the pane
/// by the time the deferred pass runs, so the fill stayed on the slot the
/// closed tab vacated. The existing retry did not help: it only fires when
/// the active item reports zero bounds, and after a removal the surviving
/// item's bounds are non-zero, just measured against the old layout.
///
/// These are wiring guards. They prove the one-shot LayoutUpdated pass is
/// still on the path a refresh takes and is still one-shot. The second
/// LayoutUpdated replay #854 armed beside it -- the strip-rooted
/// realization latch -- is gone: the realization wait now rides the
/// deferred item's own Loaded, and
/// Strip_CarriesNoStandingLayoutUpdatedSubscription pins the strip back
/// to the single settle arm plus the item latch's detach discipline.
/// Whether the fill lands on the right row is only observable on a live
/// strip, which is what scripts/mouse-fuzz-tab-close-selection.ps1 is
/// for.
/// </summary>
public class VerticalTabSelectionRowWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    private static bool IsEventHook(
        StatementSyntax statement, SyntaxKind kind, string eventName, string handler) =>
        statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
        && assignment.IsKind(kind)
        && assignment.Left.ToString() == eventName
        && assignment.Right.ToString() == handler;

    private static int IndexOfCall(SyntaxList<StatementSyntax> statements, string target) =>
        statements.TakeWhile(s => !s.Calls(target).Any()).Count();

    private static string ContainingMethod(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText;

    /// <summary>
    /// Every refresh has to arm the settle pass, and it has to do so before
    /// the coalescing early-return. Behind that guard, the one refresh that
    /// actually needed a re-place is the one that gets dropped.
    /// </summary>
    [Fact]
    public void EveryRefresh_ArmsTheSettlePass_BeforeTheCoalescingGuard()
    {
        var body = Strip().Method("ScheduleSelectionLayoutPass").Body!.Statements;

        var armIndex = IndexOfCall(body, "PlaceSelectionRowAfterLayout");
        Assert.True(
            armIndex < body.Count,
            "ScheduleSelectionLayoutPass must arm PlaceSelectionRowAfterLayout: without it "
            + "the row is placed only from layouts that may predate the change that asked "
            + "for the refresh");

        var guardIndex = body
            .TakeWhile(s => s is not IfStatementSyntax
            {
                Condition: IdentifierNameSyntax { Identifier.Text: "_selectionRefreshScheduled" }
            })
            .Count();
        Assert.True(guardIndex < body.Count, "expected the _selectionRefreshScheduled early-return");
        Assert.True(
            armIndex < guardIndex,
            "the settle pass must be armed above the _selectionRefreshScheduled early-return, "
            + "or a refresh that arrives while one is already queued never gets re-placed");
    }

    /// <summary>
    /// One-shot in both directions: subscribed only from the arm, and
    /// unsubscribed before the handler touches layout again. Placing the row
    /// invalidates layout, so a handler still attached re-enters on the pass
    /// it just caused, and a subscription never removed is the standing
    /// LayoutUpdated hook this control refuses to carry.
    /// </summary>
    [Fact]
    public void SettleHook_UnsubscribesBeforeItPlacesTheRow()
    {
        var strip = Strip();

        Assert.Contains(
            strip.Method("PlaceSelectionRowAfterLayout").Body!.Statements,
            s => IsEventHook(
                s, SyntaxKind.AddAssignmentExpression,
                "LayoutUpdated", "OnSelectionRowPlacementSettled"));

        var handler = strip.Method("OnSelectionRowPlacementSettled").Body!.Statements;
        var unhook = handler.FirstOrDefault(s => IsEventHook(
            s, SyntaxKind.SubtractAssignmentExpression,
            "LayoutUpdated", "OnSelectionRowPlacementSettled"));
        Assert.True(unhook is not null, "the settle handler must detach itself");

        var placeIndex = IndexOfCall(handler, "UpdateSelectionRow");
        Assert.True(placeIndex < handler.Count, "the settle handler must place the row");
        Assert.True(
            handler.IndexOf(unhook!) < placeIndex,
            "detach before placing: setting Canvas.Top invalidates layout and would "
            + "re-enter a handler that is still attached");
    }

    /// <summary>
    /// LayoutUpdated fires for every layout pass anywhere in the window, so
    /// the standing subscription this control refuses to carry is one that
    /// is attached for the strip's whole life. #854 armed a second replay
    /// beside the settle arm -- a strip-rooted realization latch on
    /// LayoutUpdated -- and #858 evolved this fact to a count of two with
    /// the latch's detach discipline pinned prong by prong. That latch has
    /// been replaced: the realization wait now rides the deferred item's
    /// own Loaded (_selectionRealizationItem), the precise realization
    /// event, so the strip is back to exactly one `LayoutUpdated +=` --
    /// the settle arm -- with zero standing subscriptions beyond it, and
    /// the item latch needs no Unloaded teardown because the subscription
    /// dies with the element it rides. This supersedes the #858 count-two
    /// contract; the detach discipline it demanded of the old latch is
    /// re-pinned here against the item's Loaded handler, and the latch's
    /// full anatomy is pinned by
    /// TheRealizationLatch_RidesTheItem_AndDetachesWhenLanded.
    /// </summary>
    [Fact]
    public void Strip_CarriesNoStandingLayoutUpdatedSubscription()
    {
        var strip = Strip();

        var adds = strip.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Left.ToString() == "LayoutUpdated")
            .ToList();

        var settleArm = Assert.Single(adds);
        Assert.Equal("OnSelectionRowPlacementSettled", settleArm.Right.ToString());
        Assert.Equal("PlaceSelectionRowAfterLayout", ContainingMethod(settleArm));

        // The realization wait that used to be the second LayoutUpdated arm
        // rides the item now: DeferSelectionSync subscribes the deferred
        // item's own Loaded, behind the same-item early-return, so a pass
        // that re-defers onto the item it already latched re-arms nothing
        // instead of stacking handlers on one element.
        var itemArm = Assert.Single(
            strip.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                 && a.Left.ToString() == "item.Loaded");
        Assert.Equal("OnSelectionRealized", itemArm.Right.ToString());
        Assert.Equal("DeferSelectionSync", ContainingMethod(itemArm));

        // Detach-proof, the discipline #858 pinned on the old latch,
        // carried over to the item latch: the defensive detach ahead of
        // the arm (so a re-latch of the same element after an external
        // unload cannot double-subscribe) and the landed detach in the
        // Loaded handler. Nowhere else -- an Unloaded teardown prong is
        // absent on purpose, because the handler rides the item, not the
        // strip, and dies with it.
        var detaches = strip.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                        && a.Left.ToString() == "item.Loaded"
                        && a.Right.ToString() == "OnSelectionRealized")
            .ToList();
        Assert.True(
            detaches.Count == 2,
            $"expected exactly two `item.Loaded -=` detaches (the pre-arm reset and the landed "
                + $"detach), found {detaches.Count}");
        Assert.Contains(detaches, d => ContainingMethod(d) == "DeferSelectionSync");
        var landed = detaches.Single(d => ContainingMethod(d) == "OnSelectionRealized");

        // The landed detach must sit inside the sender-is-item guard: the
        // handler can only unhook the element that raised it, and a detach
        // hoisted outside the pattern would not compile against the right
        // identifier anyway -- pin the shape so a rewrite cannot quietly
        // detach some other latch.
        Assert.True(
            landed.Ancestors().OfType<IfStatementSyntax>().Any(guard =>
                guard.Condition is IsPatternExpressionSyntax pattern
                && pattern.Expression.ToString() == "sender"),
            "the landed detach must live inside the `sender is NavigationViewItem item` guard "
            + "of the Loaded handler");
    }

    /// <summary>
    /// A close raises no ActiveTabChanged when the closed tab was not the
    /// active one (see TabCloseSelectionIdentityTests), so the collection
    /// change is the only thing that reaches the strip. That call sits
    /// outside the switch on purpose: every action reorders rows.
    /// </summary>
    [Fact]
    public void EveryCollectionChange_ReachesTheSelectionRefresh()
    {
        var strip = Strip();

        Assert.Contains(
            strip.Method("OnTabsCollectionChanged").Body!.Statements,
            s => s.Calls("SyncSelectionFromManager").Any());

        Assert.Single(strip.Method("SyncSelectionFromManager").Calls("ScheduleSelectionLayoutPass"));
    }
}
