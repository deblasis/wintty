using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pinned panel's structure (spec 5.1): a fixed, non-scrolling section
/// above the list, headed by a small-caps header, holding icon-only rows
/// for the pinned prefix. The strip now has TWO row containers, and the
/// guards here pin the seams that two containers create: where the shelf
/// is hosted, who owns order and membership, how a row is measured, and
/// the one trap a fixed section above the scroller sets for the drag
/// machine's autoscroll.
///
/// Wiring guards, not behaviour tests: whether the panel paints on the
/// right pixels is only observable on a live strip.
/// </summary>
public class PinnedPanelWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

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
    /// "Pinned" is a section heading, not a row: it carries heading
    /// semantics, it is named for find-by-name, and it is chrome of the
    /// shelf rather than an item of the row panel -- a client that walks
    /// the panel's children must find tabs, never the title.
    /// </summary>
    [Fact]
    public void TheHeader_IsAHeading_AndNotARowOfThePanel()
    {
        var build = Strip().Method("BuildPinnedShelf");

        var named = build.Calls("AutomationProperties.SetName").Single();
        Assert.Equal("_pinnedHeader", named.Arg(0));
        Assert.Equal("\"Pinned\"", named.Arg(1));

        var heading = build.Calls("AutomationProperties.SetHeadingLevel").Single();
        Assert.Equal("_pinnedHeader", heading.Arg(0));
        Assert.Equal("AutomationHeadingLevel.Level2", heading.Arg(1));

        // The shelf's own children: header, then the row panel, then the
        // boundary stroke. The header must not land inside the row panel,
        // where it would read as one of the things it titles.
        var adds = build.Calls("_pinnedShelf.Children.Add")
            .Select(c => c.Arg(0))
            .ToList();
        Assert.Equal(new[] { "_pinnedHeader", "_pinnedPanel", "_boundaryStroke" }, adds);
        Assert.Empty(build.Calls("_pinnedPanel.Children.Add"));

        // BuildPinnedShelf only parks the header collapsed as the initial
        // state; the anyPins gate in the chrome refresh is the one thing
        // that ever flips it. Asserted on the ternary's polarity, because a
        // header that never becomes visible is 24px of chrome and a heading
        // no client ever finds -- the build-time collapse alone passes a
        // visibility-blind guard.
        var shelf = Strip().Method("UpdatePinnedShelfChrome");
        var headerVisible = shelf.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_pinnedHeader.Visibility");
        Assert.Equal(
            "anyPins ? Visibility.Visible : Visibility.Collapsed",
            headerVisible.Right.ToString());
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
