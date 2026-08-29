using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The strip-order bridge between <c>TabManager</c> and the two strip
/// hosts: the drag lifecycle, the reconcile, the vertical rebuild. The
/// shell cannot load into this test host, so these guards parse it; the
/// behaviour itself is tested outright in TabStripProjectionTests. What
/// a parse adds is the polarity each route needs.
/// </summary>
public sealed class TabStripSyncWiringTests
{
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";
    private const string TabHostXaml = "Tabs.TabHost.xaml";
    private const string VerticalSource = "Tabs.VerticalTabStrip.xaml.cs";

    // --- MoveItem: the no-op guard ---

    [Fact]
    public void MoveItem_declines_an_item_already_at_its_target_before_touching_the_strip()
    {
        var moveItem = ShellSource.Load(TabHostSource).Method("MoveItem");

        // The live index comes off the strip, not out of the event: the
        // event's indices are the raw op's, which TabView has usually
        // applied already by the time this handler runs.
        var liveIndex = moveItem.DescendantNodes()
            .OfType<EqualsValueClauseSyntax>()
            .Select(v => v.Value)
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("TabItems.IndexOf", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            liveIndex.Count == 1,
            $"MoveItem should read the item's index from TabItems once; found {liveIndex.Count}.");

        // The event's index counts tabs; the strip's slots also hold
        // chips. The raw `to` never reaches a comparison with a strip
        // index -- it crosses the projection's forward mapping first,
        // which is the polarity the in-place guard below depends on.
        Assert.True(
            moveItem.Calls("TabStripProjection.ModelIndexToVisibleIndex").Count == 1,
            "MoveItem must translate the event's raw index through the " +
            "projection's forward mapping; a raw tab index is not a slot index.");

        var firstMutation = moveItem.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));

        var guard = moveItem.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsAlreadyInPlaceGuard)
            .ToList();
        Assert.True(
            guard.Count == 1 && guard[0].SpanStart < firstMutation.SpanStart,
            "MoveItem must decline an in-place item before its first TabItems.Remove; "
            + "equality on purpose, since the inverted form skips the move it exists to skip.");

        // An unbraced if body IS the return statement rather than a block
        // containing one, so accept either shape.
        var body = guard.Count == 1 ? guard[0].Statement : null;
        var bailed = body is ReturnStatementSyntax { Expression: null }
            || body?.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression is null) == true;
        Assert.True(bailed, "The in-place guard must return, not fall through to the move.");
    }

    [Fact]
    public void MoveItem_lets_the_projector_own_the_final_word()
    {
        var moveItem = ShellSource.Load(TabHostSource).Method("MoveItem");

        // TabMoved carries the raw op's indices and Normalize may have
        // relocated tabs after it, so no path through MoveItem is ever
        // the last writer.
        var firstMutation = moveItem.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));
        var guard = moveItem.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsAlreadyInPlaceGuard)
            .ToList();
        var reconcile = moveItem.Calls("ReconcileStripOrder").ToList();

        Assert.True(
            reconcile.Count == 2,
            "MoveItem must reconcile on BOTH paths: an in-place index for the moved "
            + "item does not mean Normalize did not repair the rest of the strip "
            + $"(group re-gather). Found {reconcile.Count}; one call means a path returns early.");
        Assert.True(
            guard.Count == 1 && reconcile.Any(r =>
                r.SpanStart > guard[0].SpanStart && r.Span.End < guard[0].Span.End),
            "The in-place early return must run ReconcileStripOrder before returning.");
        Assert.True(
            reconcile.Any(r => r.SpanStart > firstMutation.SpanStart),
            "The moved path must reconcile after its own mutations.");
    }

    // --- Drag lifecycle: seam cover and the drop reconcile ---

    [Fact]
    public void Drag_start_hides_the_seam_cover_and_the_drop_replaces_it()
    {
        var src = ShellSource.Load(TabHostSource);
        var starting = src.Method("OnTabDragStarting");
        var completed = src.Method("OnTabDragCompleted");

        Assert.True(
            SetsFlag(starting, "_stripDragActive", SyntaxKind.TrueLiteralExpression),
            "OnTabDragStarting must raise _stripDragActive (audit 3.2, item 1).");
        Assert.True(
            SetsFlag(completed, "_stripDragActive", SyntaxKind.FalseLiteralExpression),
            "OnTabDragCompleted must lower _stripDragActive so the cover is placed again.");

        // Synchronous, not queued: a dispatcher hop paints one frame
        // against the stale slot. Nothing else runs between the flag
        // going up and the drag, so deleting the hide must fail here.
        var hide = starting.Calls("SelectedTabSeamChanged?.Invoke").ToList();
        Assert.True(
            hide.Count == 1
                && hide[0].Arg(0) == "0" && hide[0].Arg(1) == "0" && hide[0].Arg(2) == "null",
            "OnTabDragStarting must hide the cover synchronously with (0, 0, null).");

        // Wired even though TabHost.xaml predates it: the drag start is
        // where the stale-slot window opens.
        var ctor = src.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");
        var hooked = ctor.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                        && a.Left.ToString().EndsWith("TabDragStarting", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            hooked.Count == 1,
            "The constructor must subscribe OnTabDragStarting on TabViewControl.TabDragStarting.");
    }

    [Fact]
    public void The_seam_cover_stays_down_while_a_drag_holds_the_strip()
    {
        var bridge = ShellSource.Load(TabHostSource).Method("UpdateSelectedTabBridge");

        // The gate reads the flag bare and hides the cover itself, ahead
        // of the placement math. The condition is matched as a parsed bare
        // identifier, not a substring: the inverted gate mentions the same
        // flag but parses as a prefix unary expression.
        var gate = bridge.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is IdentifierNameSyntax id
                        && id.Identifier.ValueText == "_stripDragActive")
            .ToList();
        Assert.True(
            gate.Count == 1,
            "UpdateSelectedTabBridge needs one bare `if (_stripDragActive)` gate.");

        var placement = bridge.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().Contains("TransformToVisual", StringComparison.Ordinal));
        var hide = gate.Count == 1
            ? gate[0].Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(c => c.CalleeText() == "SelectedTabSeamChanged?.Invoke")
            : null;
        Assert.True(
            hide is not null
                && hide.Arg(0) == "0" && hide.Arg(1) == "0" && hide.Arg(2) == "null"
                && gate[0].SpanStart < placement.SpanStart,
            "The drag gate must hide the cover with (0, 0, null), ahead of the placement math.");
    }

    [Fact]
    public void The_drop_reconciles_even_when_the_manager_refused_the_move()
    {
        var completed = ShellSource.Load(TabHostSource).Method("OnTabDragCompleted");

        var reconcile = completed.Calls("ReconcileStripOrder").ToList();
        Assert.True(
            reconcile.Count == 1,
            "OnTabDragCompleted must reconcile the strip to the manager's order.");
        Assert.Empty(reconcile[0].Ancestors().OfType<IfStatementSyntax>());

        // The refused drop raises no TabMoved, so nothing else would run
        // the reconcile: a clamp against the pin boundary (PR 4's grammar)
        // leaves TabView's own reorder standing unless the reconcile is
        // unconditional.
    }

    // --- The reconcile is defensive at the UI boundary ---

    [Fact]
    public void A_reconcile_failure_logs_and_rebuilds_instead_of_dying()
    {
        var reconcile = ShellSource.Load(TabHostSource).Method("ReconcileStripOrder");

        var rebuild = reconcile.Calls("RebuildStripFromManager").ToList();
        Assert.True(
            rebuild.Count == 1,
            "ReconcileStripOrder must fall back to RebuildStripFromManager: skew is "
            + "currently impossible, but a terminal's strip must not die.");
        var catchClause = rebuild.Count == 1
            ? rebuild[0].Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault()
            : null;
        Assert.True(
            catchClause is not null,
            "The rebuild fallback must be reached from a catch, not called inline.");
        var filter = catchClause?.Filter?.ToString() ?? "";
        Assert.True(
            filter.Contains("InvalidOperationException", StringComparison.Ordinal)
                && filter.Contains("KeyNotFoundException", StringComparison.Ordinal),
            "The catch must be filtered to the skew family (Diff's throws and the "
            + "item-map lookup's miss); a bare catch swallows unrelated UI failures.");

        var rebuildMethod = ShellSource.Load(TabHostSource).Method("RebuildStripFromManager");
        Assert.True(
            rebuildMethod.Calls("TabStripProjection.HorizontalRows").Count == 1,
            "The rebuild must take its order from the projector's horizontal " +
            "reading, like the reconcile does: chips occupy slots, so the " +
            "flat Rows list would drop every chip from the strip.");
    }

    [Fact]
    public void The_reconcile_never_leaks_a_dropped_selection_as_an_activation()
    {
        var reconcile = ShellSource.Load(TabHostSource).Method("ReconcileStripOrder");

        var firstMutation = reconcile.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));

        // ListView drops the selection when the selected item is removed
        // and does not restore it on re-insert; live, that drop arrives as
        // an activation of whatever TabView picked instead.
        var assigns = reconcile.AssignsTo("_suppressSelectionEvent").ToList();
        var arms = assigns.Where(a => a.Right.IsKind(SyntaxKind.TrueLiteralExpression)).ToList();
        var disarms = assigns.Where(a => a.Right.IsKind(SyntaxKind.FalseLiteralExpression)).ToList();
        Assert.True(
            arms.Count == 1 && arms[0].SpanStart < firstMutation.SpanStart,
            "ReconcileStripOrder must arm _suppressSelectionEvent before its first removal.");
        Assert.True(
            disarms.Count == 1 && disarms[0].SpanStart > firstMutation.SpanStart,
            "ReconcileStripOrder must disarm _suppressSelectionEvent after the loop.");

        var activate = reconcile.Calls("SelectActive").ToList();
        Assert.True(
            activate.Count == 1 && activate[0].SpanStart > firstMutation.SpanStart,
            "ReconcileStripOrder must call SelectActive after the loop to re-assert "
            + "the manager's active tab.");
    }

    // --- Vertical strip reads its order from the projector ---

    [Fact]
    public void Vertical_rebuild_takes_its_order_from_the_projector()
    {
        var rebuild = ShellSource.Load(VerticalSource).Method("RebuildAllItems");

        // Group-aware since the headers rung: the flat Rows list cannot
        // name a header, and a rebuild that walked it would drop every
        // group's header row from the strip.
        Assert.Single(rebuild.Calls("TabStripProjection.GroupedRows"));
        Assert.Empty(rebuild.Calls("TabStripProjection.Rows"));
    }

    // --- Unwired events: no detach path, no re-entrant validation ---

    [Fact]
    public void TabDroppedOutside_stays_unwired()
    {
        // Detach-by-drag is non-goal N1: a drop outside the strip cancels
        // and nothing consumes the event, so any handler is the bypass
        // path. Refused in both places one could appear -- code-behind,
        // which Subscribers sweeps, and the XAML attribute, which compiles
        // into a .g.cs that sweep cannot see (pinned below).
        Assert.Empty(Subscribers("TabDroppedOutside"));

        Assert.False(
            ReadEmbedded(TabHostXaml).Contains("TabDroppedOutside", StringComparison.Ordinal),
            "TabHost.xaml wires TabDroppedOutside; the drag bridge must stay a "
            + "reorder bridge, not grow a detach path.");
    }

    [Fact]
    public void TabItemsChanged_stays_unwired_and_the_reason_lives_at_the_wiring_site()
    {
        // It fires for the hosts' own writes as much as for TabView's, so
        // a validation handler on it re-enters the reconcile that mutates
        // TabItems. If one ever lands it needs a re-entrancy fence and a
        // reason the existing validation points do not cover.
        Assert.Empty(Subscribers("TabItemsChanged"));

        var ctor = ShellSource.Load(TabHostSource).Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");
        Assert.True(
            ctor.ToString().Contains("TabItemsChanged", StringComparison.Ordinal),
            "The decision to leave TabItemsChanged unwired must be recorded in the "
            + "constructor it concerns, where the next reader of the event list looks.");

        // The sweep above reads code-behind only; a XAML attribute compiles
        // into a .g.cs it can never see, so the markup is pinned directly.
        Assert.False(
            ReadEmbedded(TabHostXaml).Contains("TabItemsChanged", StringComparison.Ordinal),
            "TabHost.xaml wires TabItemsChanged; the census above cannot see a XAML attribute.");
    }

    // Every shell file subscribing the event via +=, by resource name.
    private static List<string> Subscribers(string eventName)
    {
        var found = new List<string>();
        foreach (var (resource, root) in ShellSource.AllShellSources())
            if (root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Any(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                              && a.Left.ToString().EndsWith(eventName, StringComparison.Ordinal)))
                found.Add(resource);
        return found;
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    // An equality check between the item's current slot and the target.
    // The target is the projection-mapped slot, not the event's raw `to`:
    // chips occupy slots, so a tab index is not a slot index (chips rung).
    private static bool IsAlreadyInPlaceGuard(IfStatementSyntax ifStatement)
        => ifStatement.Condition is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.EqualsExpression)
            && binary.ToString().Contains("current", StringComparison.Ordinal)
            && binary.ToString().Contains("slot", StringComparison.Ordinal);

    private static bool SetsFlag(MethodDeclarationSyntax method, string field, SyntaxKind literal)
        => method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax id
                      && id.Identifier.ValueText == field
                      && a.Right.IsKind(literal));
}
