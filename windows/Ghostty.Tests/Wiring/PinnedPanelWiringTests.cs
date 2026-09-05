using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pinned panel's structure: a fixed, non-scrolling section above
/// the list, announced by structure rather than a label or a rule -- a
/// band of icon squares that wraps, at every pane width. The guards here
/// pin the seams the two row containers create: where the shelf is
/// hosted, who owns order and membership, how a row is measured, the
/// zone's visual anchor, and the one trap a fixed section above the
/// scroller sets for the drag machine's autoscroll.
///
/// Wiring guards, not behaviour tests: whether the panel paints on the
/// right pixels is only observable on a live strip.
/// </summary>
public class PinnedPanelWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    // Assignments spelled through a member (the band's own chrome)
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
    /// The zone is announced by structure, not a label and not a rule: no
    /// header element is built, named, or gated anywhere in the shelf's
    /// wiring, and the shelf's one child is the band. The per-square
    /// "Pinned" ItemStatus (PinnedRows_KeepTheirNameAndStatus) is what
    /// keeps the zone in the automation tree.
    /// </summary>
    [Fact]
    public void TheZone_IsAnnouncedByStructure_WithNoHeaderLabel()
    {
        var build = Strip().Method("BuildPinnedShelf");

        // The shelf's own child: the band, and nothing else. A label or a
        // rule would each be a second answer to a question the anatomy
        // already settles.
        var adds = build.Calls("_pinnedShelf.Children.Add")
            .Select(c => c.Arg(0))
            .ToList();
        Assert.Equal(new[] { "_pinnedPanel" }, adds);
        Assert.Empty(build.Calls("_pinnedPanel.Children.Add"));

        // And the header is gone from the class, not just from the build:
        // a field kept "for later" is a label that comes back.
        var source = Strip().Root.ToString();
        Assert.DoesNotContain("_pinnedHeader", source, StringComparison.Ordinal);

        // The chrome refresh drives the shelf's remaining states and
        // nothing header-shaped.
        var shelf = Strip().Method("UpdatePinnedShelfChrome");
        Assert.DoesNotContain("Header", shelf.ToFullString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The band IS the zone's anchor: a different shape from the list, so
    /// nothing is drawn between them. This guard is mostly an absence --
    /// the retired stroke has to stay retired, because a rule redrawn
    /// beside a structural division is the exact double statement the
    /// shape was chosen to replace -- plus the one positive fact the
    /// absence rests on: the shelf's panel is the band, and the band's
    /// geometry comes from the shared arithmetic rather than a second
    /// copy of the numbers.
    /// </summary>
    [Fact]
    public void TheBand_IsTheZoneAnchor()
    {
        var source = Strip().Root.ToString();

        // The stroke is gone from the class, not just from the build: a
        // field kept "for later" is a rule that comes back.
        Assert.DoesNotContain("_boundaryStroke", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundaryStroke", source, StringComparison.Ordinal);

        // The band is the shelf's panel, and it is the band type -- a
        // StackPanel here would spend one row per pin again.
        var (panel, _) = Strip().Field("_pinnedPanel");
        var declared = panel.Ancestors().OfType<FieldDeclarationSyntax>()
            .First().Declaration.Type.ToString();
        Assert.Equal("TabPinBandPanel", declared);

        // The band's inset in the pane: the rows' own inset on BOTH sides,
        // so the column count is what the pane can hold rather than what
        // it can hold flush to its edge. Argument by argument, because
        // dropping the trailing one is invisible until the pane width
        // happens to land exactly on a column boundary.
        var build = Strip().Method("BuildPinnedShelf");
        var margin = Assert.IsType<ObjectCreationExpressionSyntax>(
            Assignments(build, "_pinnedPanel.Margin").Single().Right);
        Assert.Equal(
            new[] { "RowInsetLeft", "RowInsetVertical", "RowInsetLeft", "BandInsetBottom" },
            margin.ArgumentList!.Arguments.Select(a => a.ToString()).ToArray());

        // One arithmetic, not two: the panel arranges from TabPinBand and
        // the drop preview asks the panel, so the ghost and the square it
        // promises cannot disagree about where the next slot is.
        var bandPanel = ShellSource.Load("Tabs.TabPinBandPanel.cs");
        Assert.Single(bandPanel.Method("SlotRect").Calls("TabPinBand.OriginOf"));
        Assert.Single(Strip().Method("BandSlotRect").Calls("_pinnedPanel.SlotRect"));

        // The column count comes from the width the PANE offered, in both
        // passes -- the argument, not merely the call. The band is
        // left-aligned, so the size it is arranged at is its own desired
        // width, and ColumnsFor of that answers "as many columns as there
        // are squares". Every square survived it (with fewer squares than
        // columns they are all in row 0 either way); the slot one PAST the
        // end did not, and that slot is the only thing the drop preview
        // draws. Three pins in a pane that fits five put the ghost on a
        // second band row while the square landed beside the last one.
        Assert.Equal("availableSize.Width",
            bandPanel.Method("MeasureOverride").Calls("TabPinBand.ColumnsFor").Single().Arg(0));
        Assert.Equal("_offeredWidth",
            bandPanel.Method("ArrangeOverride").Calls("TabPinBand.ColumnsFor").Single().Arg(0));

        // ...and the measure is the only pass that can capture it.
        Assert.Equal("availableSize.Width",
            Assignments(bandPanel.Method("MeasureOverride"), "_offeredWidth")
                .Single().Right.ToString());
        Assert.Empty(Assignments(bandPanel.Method("ArrangeOverride"), "_offeredWidth"));
    }

    /// <summary>
    /// A pinned square is an icon square at every pane width. It has no
    /// title column to collapse and no width threshold to answer to, and
    /// the title it gives up rides the tooltip -- which the square OWES,
    /// not merely offers: two shells of the same kind draw the same icon,
    /// so without it nothing tells them apart with a pointer.
    ///
    /// The width threshold survives for the rows that do still carry a
    /// title, on the strip itself rather than on the class that no longer
    /// has one; ApplyPaneLayout stays the choke point every width change
    /// rides.
    /// </summary>
    [Fact]
    public void PinnedSquares_AreIconOnly_AtEveryPaneWidth()
    {
        var rowSource = ShellSource.Load("Tabs.VerticalTabPinnedRow.cs");
        var square = rowSource.Root.ToString();

        // No title column, no width switch, no threshold of its own.
        Assert.DoesNotContain("ShowTitle", square, StringComparison.Ordinal);
        Assert.DoesNotContain("_textColumn", square, StringComparison.Ordinal);
        Assert.DoesNotContain("TitlePaneWidthThreshold", square, StringComparison.Ordinal);
        Assert.Empty(Strip().Method("UpdatePinnedShelfChrome")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".ShowTitle", StringComparison.Ordinal)));

        // A square: one edge, and it is the band's, so the panel, the
        // ghost and the harness all measure against one number. Read at
        // statement level, because the icon slot inside carries the same
        // two property names in its own initializer.
        var ctor = rowSource.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>().Single();
        var edges = ctor.Body!.Statements.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression).OfType<AssignmentExpressionSyntax>().ToList();
        Assert.Equal("RowHeight",
            edges.Single(a => a.Left.ToString() == "Width").Right.ToString());
        Assert.Equal("RowHeight",
            edges.Single(a => a.Left.ToString() == "Height").Right.ToString());
        var edge = rowSource.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "RowHeight");
        Assert.Equal("TabPinBand.ChipSize", edge.Initializer!.Value.ToString());

        // The tooltip is the title's only remaining home (composed with the
        // directory, as TooltipText), and it is refreshed with the title
        // rather than set once at build.
        var refresh = rowSource.Method("Refresh");
        var tip = refresh.Call("ToolTipService.SetToolTip");
        Assert.Equal("this", tip.Arg(0));
        Assert.Equal("tab.TooltipText", tip.Arg(1));

        // The shared threshold lives on the strip now -- the body rows
        // and the group headers must still degrade at one width, or the
        // rail degrades in pieces.
        var threshold = Strip().Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ShowsTitles");
        Assert.Equal("_paneWidth >= TitlePaneWidthThreshold",
            threshold.ExpressionBody!.Expression.ToString());

        // ApplyPaneLayout is the choke point: the width lands there, the
        // anatomy pass runs on the change, and the pane stays honest.
        var layout = Strip().Method("ApplyPaneLayout");
        var stored = layout.AssignsTo("_paneWidth").Single();
        Assert.Equal("width", stored.Right.ToString());
        var anatomy = layout.Calls("ApplyPaneWidthAnatomy").Single();
        var gate = layout.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_paneWidth != width");
        Assert.True(gate.Span.Contains(anatomy.Span),
            "the anatomy pass must ride the width-changed gate");
        Assert.Single(Strip().Method("ApplyPaneWidthAnatomy")
            .Calls("UpdatePinnedShelfChrome"));

        // The icon takes the square's ink -- the same active/inactive
        // brush a body row's title follows -- so a pinned square matches
        // the list in every state the strip paints.
        var ink = rowSource.Method("ApplyInk");
        Assert.Contains(Assignments(ink, "_icon.Foreground").ToList(),
            a => a.Right.ToString() == "foreground");

        // And the selection fill takes the square's shape, not the lane's.
        // A lane-wide bar behind a 40px square marks four slots the active
        // tab does not occupy -- which is exactly what the fill did before
        // the pins stopped being rows, and is invisible until a pin is the
        // active tab.
        var place = Strip().Method("UpdateSelectionRow");
        var isSquare = place.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "square");
        Assert.Equal("item is VerticalTabPinnedRow", isSquare.Initializer!.Value.ToString());
        // Each axis to its own measurement. "StartsWith item.Actual" was
        // satisfied by both, so the two could be swapped -- benign only
        // while the square happens to be 40x40.
        foreach (var (name, axis) in new[]
                 { ("rowWidth", "item.ActualWidth"), ("rowHeight", "item.ActualHeight") })
        {
            var sized = place.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Single(v => v.Identifier.ValueText == name);
            var fork = Assert.IsType<ConditionalExpressionSyntax>(sized.Initializer!.Value);
            Assert.Equal("square", fork.Condition.ToString());
            Assert.Equal(axis, fork.WhenTrue.ToString());
        }

        // And the fill's POSITION forks too, which is where a wrapping band
        // actually differs from a lane: a square in column 1, 2 or 3 sits
        // 44, 88 or 132px along, and an unconditional Canvas.SetLeft of the
        // row inset would draw the selection on the first column every time.
        // Invisible to the geometry harness, which selects a body row.
        foreach (var setter in new[] { "Canvas.SetLeft", "Canvas.SetTop" })
        {
            var call = place.Calls(setter).Single();
            Assert.Equal("SelectionRow", call.Arg(0));
            Assert.Contains("square", call.Arg(1));
        }
        // Closed on all four sides: the folder stroke's open edge exists
        // because a body row MEETS the terminal there, and a square in the
        // middle of a band meets nothing.
        var stroke = Assert.IsType<ConditionalExpressionSyntax>(
            Assignments(place, "SelectionRow.BorderThickness").Single().Right);
        Assert.Equal("square", stroke.Condition.ToString());
        Assert.Equal("new Thickness(1)", stroke.WhenTrue.ToString());
        Assert.Equal("new Thickness(1, 1, 0, 1)", stroke.WhenFalse.ToString());
    }

    /// <summary>
    /// The band glides its squares between slots, and hands them back
    /// whenever a gesture takes over their composition Translation.
    ///
    /// This is the transition the wrapping shape creates: a pin added or
    /// removed reflows the band in BOTH axes -- a square pushed off the
    /// end of a row travels down and back to the left -- which the list's
    /// vertical-only glide could not express. It is also the one place
    /// two writers could meet, so the stand-down is pinned as hard as the
    /// motion is.
    /// </summary>
    [Fact]
    public void TheBand_Glides_AndStandsDownUnderAGesture()
    {
        var bandPanel = ShellSource.Load("Tabs.TabPinBandPanel.cs");

        // Both axes. A vertical-only delta slides a wrapping square
        // through the squares it is passing.
        var reflow = bandPanel.Method("Reflow");
        var delta = reflow.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "delta");
        var text = delta.Initializer!.Value.ToString();
        Assert.Contains("was.X - origin.X", text, StringComparison.Ordinal);
        Assert.Contains("was.Y - origin.Y", text, StringComparison.Ordinal);

        // A square arranged for the first time only records its slot: it
        // has no old slot to come from, and gliding it from the band's
        // origin flies every pin in from the corner on the first frame.
        var first = reflow.DescendantNodes().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("_lastOrigin.TryGetValue"));
        Assert.Contains("return", first.Statement.ToString(), StringComparison.Ordinal);

        // Motion off is a cut that still owes the hand-back: a square
        // stopped mid-glide keeps whatever Translation it held.
        //
        // The condition WHOLE, and the return with it. Two substrings --
        // "!MotionEnabled" in the condition and "HandBack(child)" in the
        // body -- survive turning the `||` into `&&`, which starts a
        // composition animation on every reflow with motion off, and they
        // survive deleting the `return` too, which falls straight through
        // into the animation path after handing the square back.
        var cut = reflow.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("MotionEnabled", StringComparison.Ordinal));
        Assert.Equal("!MotionEnabled || delta.LengthSquared() < 0.25f", cut.Condition.ToString());
        Assert.Contains("HandBack(child)", cut.Statement.ToString(), StringComparison.Ordinal);
        Assert.Contains("return", cut.Statement.ToString(), StringComparison.Ordinal);

        // One writer on Translation: the strip turns the band off for the
        // length of a gesture and hands the squares back before the drag
        // arms its own follow.
        //
        // An AND, asserted as one -- `&&` and `||` are both a
        // BinaryExpressionSyntax and both contain the same two substrings.
        // Under `||` the band glides while a live drag owns every row's
        // Translation, which is the two-writer collision this guard's own
        // summary says it pins as hard as the motion; under a negated gate
        // it glides only in the reduce-motion sessions that must see a cut.
        var gate = Assignments(
            Strip().Method("UpdatePinnedShelfChrome"), "_pinnedPanel.MotionEnabled").Single();
        var conjunction = Assert.IsType<BinaryExpressionSyntax>(gate.Right);
        Assert.True(conjunction.IsKind(SyntaxKind.LogicalAndExpression),
            "the band's motion needs BOTH facts: no live drag AND the strip's motion gate");
        Assert.Equal("_drag is null", conjunction.Left.ToString());
        conjunction.Right.AssertCallTo("TabStripMotion.Enabled");
        Assert.Single(Strip().Method("StartDragVisual").Calls("_pinnedPanel.StopMotion"));
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
        Assert.Equal("tab.TooltipText", tooltip.Arg(1));
    }
}
