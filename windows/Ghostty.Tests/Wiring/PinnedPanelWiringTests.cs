using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pinned panel's structure (spec 5.1, restated by the zone-visual
/// design): a fixed, non-scrolling section above the list, announced by
/// structure rather than a label -- body-row anatomy in the expanded
/// pane, icon-only in the compact pane, and one confident boundary line
/// under the last pinned row. The guards here pin the seams the two row
/// containers create: where the shelf is hosted, who owns order and
/// membership, how a row is measured, the zone's visual anchor, and the
/// one trap a fixed section above the scroller sets for the drag
/// machine's autoscroll.
///
/// Wiring guards, not behaviour tests: whether the panel paints on the
/// right pixels is only observable on a live strip.
/// </summary>
public class PinnedPanelWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    // Assignments spelled through a member (the boundary stroke's chrome)
    // -- SyntaxQueries.AssignsTo matches bare identifiers only.
    private static System.Collections.Generic.IEnumerable<AssignmentExpressionSyntax>
        Assignments(SyntaxNode node, string left)
        => node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == left);

    /// <summary>
    /// The shelf rides PaneCustomContent, and that placement is the whole
    /// design: a root-grid row would sit outside the pane and fight MUXC's
    /// pane/compact switching; a MenuItems entry would scroll with the
    /// list and join MUXC selection, which a pinned section must not do.
    /// PaneCustomContent is the slot MUXC already reserves between the
    /// pane toggle and the scrolling list.
    /// </summary>
    [Fact]
    public void TheShelf_IsHostedInPaneCustomContent_NotTheScrollingList()
    {
        var build = Strip().Method("BuildPinnedShelf");

        var placed = build.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "NavView.PaneCustomContent");
        Assert.Equal("_pinnedShelf", placed.Right.ToString());

        // Nothing about the shelf may travel through the scrolling list:
        // the container is what makes it fixed and non-selecting.
        Assert.DoesNotContain(
            build.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().Contains("MenuItems"));

        // Its chrome (header visibility, boundary alpha) is refreshed by the
        // pass every selection-placement and drag entry/exit path already
        // calls, so the two states the shelf tracks cannot go stale on a
        // path that forgets to ask.
        Assert.Single(Strip().Method("UpdateRowSeparators")
            .Calls("UpdatePinnedShelfChrome"));
    }

    /// <summary>
    /// The zone is announced by structure, not a label: no header element
    /// is built, named, or gated anywhere in the shelf's wiring, and the
    /// shelf's children are exactly the row panel and the boundary stroke.
    /// The per-row "Pinned" ItemStatus (PinnedRows_KeepTheirNameAndStatus)
    /// is what keeps the zone in the automation tree after the heading's
    /// removal.
    /// </summary>
    [Fact]
    public void TheZone_IsAnnouncedByStructure_WithNoHeaderLabel()
    {
        var build = Strip().Method("BuildPinnedShelf");

        // The shelf's own children: the row panel, then the boundary
        // stroke. Nothing else -- a label would be a second answer to a
        // question the anatomy already settles.
        var adds = build.Calls("_pinnedShelf.Children.Add")
            .Select(c => c.Arg(0))
            .ToList();
        Assert.Equal(new[] { "_pinnedPanel", "_boundaryStroke" }, adds);
        Assert.Empty(build.Calls("_pinnedPanel.Children.Add"));

        // And the header is gone from the class, not just from the build:
        // a field kept "for later" is a label that comes back.
        var source = Strip().Root.ToString();
        Assert.DoesNotContain("_pinnedHeader", source, StringComparison.Ordinal);

        // The chrome refresh drives the shelf's two remaining states --
        // visibility and the boundary -- and nothing header-shaped.
        var shelf = Strip().Method("UpdatePinnedShelfChrome");
        Assert.DoesNotContain("Header", shelf.ToFullString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The boundary line is the zone's one anchor, so its placement and
    /// presence are pinned at the parsed call sites: twice an ordinary row
    /// line, a breath below the cluster, and stopping the SAME distance
    /// short of the row band at both ends so it reads as drawn between the
    /// zones without drifting off the band's center; visible exactly while
    /// both zones exist, and painted by the brush that carries the drag's
    /// aiming feedback and the High Contrast opaque override.
    /// </summary>
    [Fact]
    public void TheBoundary_IsTheZoneAnchor()
    {
        var build = Strip().Method("BuildPinnedShelf");

        Assert.Equal("BoundaryStrokeHeight",
            Assignments(build, "_boundaryStroke.Height").Single().Right.ToString());
        // Argument by argument, because the band starts at the rows' own
        // left inset: an equal inset on both ends of the band spells as
        // RowInsetLeft + the inset on the left and the inset alone on the
        // right. Dropping either term is what puts the rule off center.
        var margin = Assert.IsType<ObjectCreationExpressionSyntax>(
            Assignments(build, "_boundaryStroke.Margin").Single().Right);
        Assert.Equal(
            new[] { "RowInsetLeft + BoundaryStrokeInset", "3", "BoundaryStrokeInset", "0" },
            margin.ArgumentList!.Arguments.Select(a => a.ToString()).ToArray());

        var shelf = Strip().Method("UpdatePinnedShelfChrome");
        var visible = Assignments(shelf, "_boundaryStroke.Visibility").Single();
        Assert.Equal("bothZones ? Visibility.Visible : Visibility.Collapsed",
            visible.Right.ToString());
        // The brush is only written under the same gate: a boundary painted
        // while hidden is chrome nobody sees, and a gate without the paint
        // is a line that never brightens.
        var paint = Assignments(shelf, "_boundaryStroke.Background").Single();
        Assert.True(visible.Span.End < paint.Span.Start,
            "the boundary's visibility gate must precede its paint");

        var brush = Strip().Method("BoundaryStrokeBrush");
        // The drag gate rides the alpha declaration: idle presence while
        // no drag holds the strip, near-full while one aims at it.
        var alpha = brush.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "alpha");
        var ternary = Assert.IsType<ConditionalExpressionSyntax>(alpha.Initializer!.Value);
        Assert.Equal("_drag is null", ternary.Condition.ToString());
        Assert.Equal("idle", ternary.WhenTrue.ToString());
        Assert.Equal("live", ternary.WhenFalse.ToString());

        // High Contrast overrides translucency: both states resolve
        // opaque there, on the system's HC accent.
        var declarators = brush.DescendantNodes().OfType<VariableDeclaratorSyntax>().ToList();
        Assert.Contains(declarators, v => v.Identifier.ValueText == "idle"
            && v.Initializer!.Value.ToString().Contains("_highContrast")
            && v.Initializer.Value.ToString().Contains("0xFF"));
        Assert.Contains(declarators, v => v.Identifier.ValueText == "live"
            && v.Initializer!.Value.ToString().Contains("0xFF"));
    }

    /// <summary>
    /// The pinned row's anatomy follows the pane: full body-row anatomy --
    /// icon, title, bell -- once the pane is wide enough to read a
    /// trimmed title, and the icon-only slot the compact pane fits below
    /// that. The strip drives it from ApplyPaneLayout, the one pass every
    /// width change rides, and the row degrades by collapsing the title
    /// column, never by re-building the row.
    /// </summary>
    [Fact]
    public void PinnedRows_WearBodyAnatomy_WhenThePaneIsWide()
    {
        var shelf = Strip().Method("UpdatePinnedShelfChrome");
        var flip = Assignments(shelf, "row.ShowTitle").Single();
        // The shared threshold, not a local copy: the shelf, the body rows
        // and the group headers must degrade at one width or the rail
        // degrades in pieces.
        Assert.Equal("ShowsTitles", flip.Right.ToString());
        var threshold = Strip().Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ShowsTitles");
        Assert.Equal("_paneWidth >= VerticalTabPinnedRow.TitlePaneWidthThreshold",
            threshold.ExpressionBody!.Expression.ToString());

        // ApplyPaneLayout is the choke point: the width lands there, the
        // anatomy pass runs on the change, and the pane stays honest.
        var layout = Strip().Method("ApplyPaneLayout");
        var stored = layout.AssignsTo("_paneWidth").Single();
        Assert.Equal("width", stored.Right.ToString());
        var refresh = layout.Calls("ApplyPaneWidthAnatomy").Single();
        var gate = layout.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_paneWidth != width");
        Assert.True(gate.Span.Contains(refresh.Span),
            "the anatomy pass must ride the width-changed gate");
        Assert.Single(Strip().Method("ApplyPaneWidthAnatomy")
            .Calls("UpdatePinnedShelfChrome"));

        // The row degrades structurally: the title column collapses, and
        // the bell re-parents between the icon slot's corner and the
        // title column's trailing edge -- the two states a compact pane
        // and an expanded one wear.
        var rowSource = ShellSource.Load("Tabs.VerticalTabPinnedRow.cs");
        var showTitle = rowSource.Root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ShowTitle");
        var setter = showTitle.AccessorList!.Accessors
            .Single(a => a.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration));
        var collapse = setter.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_textColumn.Visibility");
        Assert.Contains("value", collapse.Right.ToString(), StringComparison.Ordinal);
        Assert.Contains("_iconSlot.Children.Remove(_bell)",
            setter.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("_textColumn.Children.Add(_bell)",
            setter.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("_iconSlot.Children.Add(_bell)",
            setter.ToFullString(), StringComparison.Ordinal);

        // And the title takes the row's ink -- the same active/inactive
        // brush the icon follows -- so a pinned row's title matches a body
        // row's in every state the strip paints.
        var ink = rowSource.Method("ApplyInk");
        Assert.Contains(Assignments(ink, "_title.Foreground").ToList(),
            a => a.Right.ToString() == "foreground");
    }

    /// <summary>
    /// Rows live in two containers now, and the resolver is the seam: it
    /// is the one place that knows which container holds a tab, and every
    /// measurement and drag read goes through it, so both containers sit
    /// in the same coordinate space -- the strip root -- and the drag
    /// machine's arranged-center arithmetic keeps working across a zone
    /// crossing untouched. A resolver that lost a container would make
    /// pinned rows unmeasurable: no selection fill, no drag, no separator
    /// gaps.
    /// </summary>
    [Fact]
    public void RowsInBothContainers_MeasureAgainstTheStripRoot()
    {
        var strip = Strip();
        var resolve = strip.Method("RowElementOf");

        // Both containers, pinned first: a row is where the pinned registry
        // says it is, else where the body registry says it is.
        Assert.Contains(resolve.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => m.Expression.ToString() == "_pinnedRows");
        Assert.Contains(resolve.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => m.Expression.ToString() == "_items");

        // And the reads that define the coordinate space route through it,
        // rather than reaching into one container directly.
        foreach (var method in new[]
                 {
                     "RowCenterY", "StartDragVisual", "RebindFollow", "GlideRow",
                     "TabElement", "UpdateSelectionRow",
                 })
        {
            var body = strip.Method(method);
            Assert.Single(body.Calls("RowElementOf"));
            Assert.DoesNotContain(
                body.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                m => m.Expression.ToString() == "_items");
        }
    }

    /// <summary>
    /// Membership comes from the collection events, order from the
    /// projection: with pinned rows outside MenuItems, a manager index
    /// counts slots the list does not hold, so the event index can no
    /// longer be the order authority for the body. The reconcile applies
    /// the projection to both containers, repairs membership skew with a
    /// rebuild, fences the MUXC list it reorders, and refreshes the shelf.
    /// Group headers join the projection's output: the desired sequence and
    /// the visible set are one GroupedRows pass.
    /// </summary>
    [Fact]
    public void Order_ComesFromTheProjection_AndSkewRebuilds()
    {
        var reconcile = Strip().Method("ReconcileRowOrder");

        // One consult, and it is the group-aware walk: a header projected
        // but not rendered, or a hidden member the strip still shows, is
        // exactly the drift this walk exists to catch.
        Assert.Single(reconcile.Calls("TabStripProjection.GroupedRows"));
        Assert.Empty(reconcile.Calls("TabStripProjection.Rows"));
        Assert.Single(reconcile.Calls("UpdatePinnedShelfChrome"));
        // The drift gate's rebuild routes through the retry executor (the
        // attempt is RebuildAllItems, pinned by the vertical drag's own
        // fact) -- a bare rebuild lands on MUXC's frames and wedges.
        Assert.Single(reconcile.Calls("ReconcileRetry.Rebuild"));

        // Skew is checked against BOTH registries, plus the rows the
        // projection named but the strip holds no element for -- counts can
        // agree while a row is missing on both sides, and only the walk's
        // miss flag sees that. The body-row expectation is the
        // projection's rendered count (shown.Count), NOT tabs-minus-pinned:
        // a chip'd run hides members on purpose, and a formula that counts
        // them would disagree with the rebuild's own output every pass
        // forever. Dropping any of these passes a drifted container
        // straight into an indexer miss or a wrong order.
        var skew = reconcile.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.DescendantNodes().OfType<InvocationExpressionSyntax>()
                             .Any(c => c.CalleeText() == "ReconcileRetry.Rebuild"));
        var condition = skew.Condition.ToString();
        Assert.Contains("missing", condition);
        Assert.Contains("_pinnedRows.Count != pinCount", condition);
        Assert.Contains("_items.Count != shown.Count", condition);
        Assert.Contains("_pinnedPanel.Children.Count != pinCount", condition);
        Assert.Contains("NavView.MenuItems.Count != desired.Count", condition);

        // The MUXC list is fenced while the reconcile mutates it: reordering
        // under a live selection raises SelectionChanged for a row the user
        // did not pick, and that reaches activation.
        var fence = reconcile.AssignsTo("_syncing")
            .Where(a => a.Right.ToString() == "true")
            .ToList();
        Assert.Single(fence);
        var firstInsert = reconcile.Calls("items.Insert").Single();
        Assert.True(fence[0].Span.Start < firstInsert.Span.Start,
            "the reconcile must fence MUXC selection before its first list insert");
    }

    /// <summary>
    /// A click on a shelf row has no MUXC SelectionChanged behind it -- the
    /// panel is outside selection -- so the drag machine's sub-threshold
    /// release is the only thing that can carry the activation. The
    /// press-time row decides which container the click landed in (by
    /// release time a zone churn may have moved it), and body clicks keep
    /// flowing through MUXC untouched.
    /// </summary>
    [Fact]
    public void ClickingAShelfRow_ActivatesThroughTheSubThresholdRelease()
    {
        var released = Strip().Method("DragRelease");

        // The guard names the press-time container. Dropping the conjunct
        // activates on every sub-threshold release -- body rows would then
        // activate twice, once here and once through MUXC; inverting it
        // never does, which is the missing-activation regression itself.
        var click = released.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                .Contains("drag.PressRow is VerticalTabPinnedRow"));

        // The activation rides the one shared shelf seam -- Enter/Space
        // takes the same path -- so both gestures are guaranteed the same
        // fence. The seam's own polarity is pinned with the focus facts.
        var activate = click.Calls("ActivateFromShelf").Single();
        Assert.Equal("drag.Tab", activate.Arg(0));
    }

    /// <summary>
    /// The seam cover clips to the container the active row lives in. A
    /// pinned active row sits above the scroller, so clamping it to the
    /// scrolling viewport produces an empty span and the cover collapses --
    /// the pane-border join silently disappears for exactly the tabs the
    /// panel exists to hold. Asserted on the branch shapes: the pinned arm
    /// must take the shelf and nothing else may.
    /// </summary>
    [Fact]
    public void TheSeamViewport_ClipsToTheContainerTheActiveRowLivesIn()
    {
        var viewport = Strip().Method("SelectionViewport");

        var pick = viewport.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Single(c => c.Condition.ToString().Contains("VerticalTabPinnedRow"));
        Assert.Contains("_pinnedShelf", pick.WhenTrue.ToString());
        Assert.Contains("_menuItemsScroller", pick.WhenFalse.ToString());
    }

    /// <summary>
    /// The panel is fixed ABOVE the scroller, so a pointer resting on it is
    /// above the viewport: to the machine that reads as fromTop negative,
    /// the deepest point of the scroll-up band, and the strip would
    /// phantom-scroll up at full speed under a stationary finger. The band
    /// is defined to start at the scroller's top edge and never above it --
    /// the guard is before the speed computation, so the band arithmetic is
    /// never even reached with a pointer outside the scroller.
    /// </summary>
    [Fact]
    public void ThePanelPointer_DoesNotAutoscroll()
    {
        var tick = Strip().Method("OnAutoscrollTick");

        var guard = tick.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "drag.LastPointerY < top");
        Assert.True(
            guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(),
            "a pointer above the scroller must stand down, not just slow down");

        var speed = tick.Calls("drag.Machine.AutoscrollSpeed").Single();
        Assert.True(guard.Span.Start < speed.Span.Start,
            "the guard must precede the band computation: AutoscrollSpeed with a "
            + "negative fromTop is the phantom scroll itself");
        Assert.Equal("top", speed.Arg(1));
    }

    /// <summary>
    /// The drag arms on rows in EITHER container: a press on a pinned row
    /// must resolve the tab through the pinned row's ancestry, and the
    /// element-ownership guard must ask the resolver, not the body
    /// registry -- a body-only check would refuse to lift pinned rows, or
    /// worse, lift a stale element the strip no longer owns.
    /// </summary>
    [Fact]
    public void TheDrag_ArmsOnARowInEitherContainer()
    {
        var pressed = Strip().Method("DragPress");

        // The pinned row ancestry is in the resolution chain.
        Assert.Contains(pressed.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().Contains("FindAncestor<VerticalTabPinnedRow>"));

        // And ownership is the resolver's answer compared by identity.
        var guard = pressed.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("ReferenceEquals(owned, item)"));
        Assert.Contains("RowElementOf(tab) is not { } owned", guard.Condition.ToString());

        // The same rule downstream: the churn path re-resolves through the
        // resolver too, so a zone crossing that moves the dragged row into
        // the panel rebinds the follow on the row's new element.
        Assert.Equal(2, Strip().Method("EvaluateDrag").Calls("RowElementOf").Count);
    }

    /// <summary>
    /// An icon-only row has no title text, so the accessible name and the
    /// "Pinned"/"Bell" status are the whole contract with an assistive
    /// client -- and they have to be re-derived when the title or the bell
    /// changes, not stamped once at build. The strip's title binding lands
    /// on Refresh, which is where that chrome is re-applied; naming the
    /// full argument text is what makes a Name/Status swap go red.
    /// </summary>
    [Fact]
    public void PinnedRows_KeepTheirNameAndStatus_ThroughRetitle()
    {
        var refresh = ShellSource.Load("Tabs.VerticalTabPinnedRow.cs").Method("Refresh");

        var name = refresh.Call("AutomationProperties.SetName");
        Assert.Equal("this", name.Arg(0));
        Assert.Equal("TabAccessibleText.Name(tab)", name.Arg(1));

        var status = refresh.Call("AutomationProperties.SetItemStatus");
        Assert.Equal("this", status.Arg(0));
        Assert.Equal("TabAccessibleText.Status(tab)", status.Arg(1));

        var tooltip = refresh.Call("ToolTipService.SetToolTip");
        Assert.Equal("this", tooltip.Arg(0));
        Assert.Equal("tab.EffectiveTitle", tooltip.Arg(1));
    }
}
