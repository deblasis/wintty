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
/// hosts: the drag lifecycle, the reconcile, and the vertical strip's
/// rebuild path.
///
/// The shell assembly cannot be loaded into this test host, so what
/// these tests can drive is the hosts' parsed source. The behaviour the
/// bridge exists for is tested outright in TabStripProjectionTests
/// (manager in, live-mirror replay out); what a parse adds here is the
/// assurance the hosts still route through it -- with the polarity each
/// route needs, which is the part a "the call exists" guard skips.
/// </summary>
public sealed class TabStripSyncWiringTests
{
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";
    private const string VerticalSource = "Tabs.VerticalTabStrip.xaml.cs";

    // --- MoveItem: the no-op guard ---

    [Fact]
    public void MoveItem_declines_an_item_already_at_its_target_before_touching_the_strip()
    {
        var moveItem = ShellSource.Load(TabHostSource).Method("MoveItem");

        // The live index comes off the strip, not out of the event: the
        // event's indices are the raw op's, and TabView's own reorder has
        // usually applied them already by the time this handler runs.
        var liveIndex = moveItem.DescendantNodes()
            .OfType<EqualsValueClauseSyntax>()
            .Select(v => v.Value)
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("TabItems.IndexOf", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            liveIndex.Count == 1,
            "MoveItem should read the item's current index from TabItems once; " +
            $"found {liveIndex.Count} such reads.");

        var firstMutation = moveItem.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));

        var guard = moveItem.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsAlreadyInPlaceGuard)
            .ToList();
        Assert.True(
            guard.Count == 1 && guard[0].SpanStart < firstMutation.SpanStart,
            "MoveItem must decline an item that is already at the target index " +
            "before its first TabItems.Remove. The guard is an equality test on " +
            "purpose: the inverted form skips exactly the move it exists to skip.");

        // An unbraced if body IS the return statement rather than a block
        // containing one, so accept either shape.
        var body = guard.Count == 1 ? guard[0].Statement : null;
        var bailed = body is ReturnStatementSyntax { Expression: null }
            || body?.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression is null) == true;
        Assert.True(
            bailed,
            "The already-in-place guard must return, not fall through to the move.");
    }

    [Fact]
    public void MoveItem_lets_the_projector_own_the_final_word()
    {
        var moveItem = ShellSource.Load(TabHostSource).Method("MoveItem");

        // TabMoved carries the raw op's indices and Normalize may have
        // relocated tabs after it, so the single-row fast path is never
        // the last writer: the reconcile after it re-derives the strip
        // from the manager's final state.
        var firstMutation = moveItem.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().EndsWith("TabItems.Remove", StringComparison.Ordinal));
        var reconcile = moveItem.Calls("ReconcileStripOrder").ToList();

        Assert.True(
            reconcile.Count == 1 && reconcile[0].SpanStart > firstMutation.SpanStart,
            "MoveItem must hand the result to ReconcileStripOrder after its own " +
            "mutations: the event's indices predate Normalize's repair.");
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
            "OnTabDragStarting must raise _stripDragActive: mid-drag the cover " +
            "would ride the stale slot for the whole drag (audit 3.2, item 1).");
        Assert.True(
            SetsFlag(completed, "_stripDragActive", SyntaxKind.FalseLiteralExpression),
            "OnTabDragCompleted must lower _stripDragActive so the cover is placed again.");

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

        // The gate reads the flag and hides the cover, and it sits ahead
        // of the placement math -- after TransformToVisual the stale slot
        // has already been measured.
        var gate = bridge.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("_stripDragActive", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            gate.Count == 1,
            "UpdateSelectedTabBridge needs one _stripDragActive gate.");

        var placement = bridge.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText().Contains("TransformToVisual", StringComparison.Ordinal));
        var hides = gate.Count == 1
            && gate[0].SpanStart < placement.SpanStart
            && gate[0].Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText() == "SelectedTabSeamChanged?.Invoke");
        Assert.True(
            hides,
            "The drag gate must hide the cover itself, ahead of the placement math.");
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

    // --- Vertical strip reads its order from the projector ---

    [Fact]
    public void Vertical_rebuild_takes_its_order_from_the_projector()
    {
        var rebuild = ShellSource.Load(VerticalSource).Method("RebuildAllItems");

        Assert.Single(rebuild.Calls("TabStripProjection.Rows"));
    }

    // --- The drop-outside invariant: no detach-by-drag bypass ---

    [Fact]
    public void TabDroppedOutside_stays_unwired()
    {
        // Detach-by-drag is non-goal N1: a drop outside the strip cancels,
        // and everything stays consistent because nothing consumes the
        // event. A handler here is the bypass path, so it is refused in
        // both places one could appear -- code-behind, which this sweep
        // reads, and the XAML attribute, which compiles into a .g.cs this
        // census cannot see and is pinned by the embedded markup below.
        var wired = new List<string>();
        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            bool any = root.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                          && a.Left.ToString().EndsWith("TabDroppedOutside", StringComparison.Ordinal));
            if (any) wired.Add(resource);
        }

        Assert.True(
            wired.Count == 0,
            "TabDroppedOutside subscriptions appeared in: " + string.Join(", ", wired)
            + ". Detach-by-drag is a non-goal; a drop outside the strip cancels.");

        Assert.False(
            ReadEmbedded("Tabs.TabHost.xaml").Contains("TabDroppedOutside", StringComparison.Ordinal),
            "TabHost.xaml wires TabDroppedOutside; the drag bridge must stay a "
            + "reorder bridge, not grow a detach path.");
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

    // --- TabItemsChanged stays unwired, with the reason at the wiring site ---

    [Fact]
    public void TabItemsChanged_stays_unwired_and_the_reason_lives_at_the_wiring_site()
    {
        // It fires for the hosts' own writes as much as for TabView's, so
        // a validation handler on it re-enters the reconcile that mutates
        // TabItems. If one ever lands it needs a re-entrancy fence and a
        // reason the two existing validation points do not cover.
        var subscribed = new List<string>();
        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            bool any = root.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken)
                          && a.Left.ToString().EndsWith("TabItemsChanged", StringComparison.Ordinal));
            if (any) subscribed.Add(resource);
        }

        Assert.True(
            subscribed.Count == 0,
            "TabItemsChanged subscriptions appeared in: " + string.Join(", ", subscribed)
            + ". A validation handler there re-enters the reconcile that mutates TabItems.");

        var ctor = ShellSource.Load(TabHostSource).Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "TabHost");
        Assert.True(
            ctor.ToString().Contains("TabItemsChanged", StringComparison.Ordinal),
            "The decision to leave TabItemsChanged unwired must be recorded in the "
            + "constructor it concerns, where the next reader of the event list looks.");
    }

    // An equality check between the item's current index and the target.
    private static bool IsAlreadyInPlaceGuard(IfStatementSyntax ifStatement)
        => ifStatement.Condition is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.EqualsExpression)
            && binary.ToString().Contains("current", StringComparison.Ordinal)
            && binary.ToString().Contains("to", StringComparison.Ordinal);

    private static bool SetsFlag(MethodDeclarationSyntax method, string field, SyntaxKind literal)
        => method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax id
                      && id.Identifier.ValueText == field
                      && a.Right.IsKind(literal));
}
