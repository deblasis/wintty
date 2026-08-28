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
/// still on the path a refresh takes and is still one-shot; whether the
/// fill lands on the right row is only observable on a live strip, which is
/// what scripts/mouse-fuzz-tab-close-selection.ps1 is for.
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
    /// The arm is the only subscriber. A second one anywhere in the file is
    /// the per-layout-pass cost the class was built to avoid, and it would
    /// also outlive the one-shot guard.
    /// </summary>
    [Fact]
    public void Strip_CarriesNoStandingLayoutUpdatedSubscription()
    {
        var adds = Strip().Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Left.ToString() == "LayoutUpdated")
            .ToList();

        Assert.True(
            adds.Count == 1,
            $"expected exactly one `LayoutUpdated +=` in VerticalTabStrip, found {adds.Count}");
        Assert.Equal(
            "PlaceSelectionRowAfterLayout",
            adds[0].Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText);
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
