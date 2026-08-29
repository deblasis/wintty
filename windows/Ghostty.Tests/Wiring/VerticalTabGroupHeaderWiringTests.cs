using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The vertical strip's group headers, as wiring guards: rows land as
/// top-level items in projection order (the spec's nested sketch cannot
/// render Edge-135), the header's only interaction is the toggle, and the
/// keyboard's shelf crossing never lands on a header. Model half: TabManagerPinGroupTests.
/// </summary>
public class VerticalTabGroupHeaderWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    private static ShellSource Header() =>
        ShellSource.Load("Tabs.VerticalTabGroupHeaderRow.cs");

    private static AssignmentExpressionSyntax Assign(SyntaxNode node, string left) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == left);

    /// <summary>The switch arms are the routing: one row kind per projection kind.</summary>
    [Fact]
    public void Headers_AreFlatTopLevelItems_InProjectionOrder()
    {
        var rebuild = Strip().Method("RebuildAllItems");
        var walk = rebuild.Calls("TabStripProjection.GroupedRows").Single();
        var cases = walk.Ancestors().OfType<ForEachStatementSyntax>().First()
            .Statement.DescendantNodes().OfType<SwitchSectionSyntax>().ToList();
        Assert.Equal(2, cases.Count);

        var headerArm = cases.Single(s => s.Labels.ToString().Contains("ProjectedRow.Header"));
        Assert.Contains("ProjectedRow.Header { Group: { } group }", headerArm.Labels.ToString());
        Assert.Contains("AddGroupRow(group);", headerArm.ToString());

        var itemArm = cases.Single(s => s.Labels.ToString().Contains("ProjectedRow.Item"));
        Assert.Contains("ProjectedRow.Item { Tab: { } tab }", itemArm.Labels.ToString());
        Assert.Contains("AddItem(tab);", itemArm.ToString());
    }

    /// <summary>
    /// A selected header would blank the active row's paint while the
    /// manager's active tab did not move; and the ItemStatus polarity is
    /// the collapse bit -- expanded reads nothing, or the state sticks.
    /// </summary>
    [Fact]
    public void TheHeaderItem_NeverSelects_AndItsChromeReadsTheCollapseBit()
    {
        var add = Strip().Method("AddGroupRow");
        var item = add.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "VerticalTabGroupHeaderItem");
        var initializers = item.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
            .ToDictionary(a => a.Left.ToString(), a => a.Right.ToString());
        Assert.Equal("group", initializers["Tag"]);
        Assert.Equal("false", initializers["SelectsOnInvoked"]);

        var chrome = add.Calls("ApplyGroupChrome").Single();
        Assert.True(chrome.Span.Start < Assign(add, "_headers[group]").Span.Start,
            "the header's chrome must be applied before the row can be reached");
        Assert.Contains("MembersOf(group).Count", item.ToString());

        var apply = Strip().Method("ApplyGroupChrome");
        var name = apply.Call("AutomationProperties.SetName");
        Assert.Equal("item", name.Arg(0));
        Assert.Equal("group.Title", name.Arg(1));

        var status = apply.Call("AutomationProperties.SetItemStatus");
        Assert.Equal("item", status.Arg(0));
        Assert.Equal(@"group.IsCollapsed ? ""Collapsed"" : string.Empty", status.Arg(1));
    }

    /// <summary>
    /// The toggle stands down under a LIVE drag, fences the MUXC list the
    /// following reconcile mutates, and routes through the manager op --
    /// never a direct IsCollapsed write (the complement polarity IS the
    /// toggle: a same-direction write is invisible).
    /// </summary>
    /// <remarks>
    /// The stand-down is phase-aware on purpose (5b-3b). A header press
    /// arms a drag session immediately, and on a plain click MUXC raises
    /// ItemInvoked from its own release handler -- deeper in the tree, so
    /// BEFORE the strip's release handler clears the still-unlifted
    /// session. The old `_drag is not null` gate ate every header click
    /// the moment header presses armed; only a gesture that actually
    /// lifted (Dragging) is a drag, and a lifted gesture holds the
    /// pointer capture, so MUXC never raises ItemInvoked for it at all.
    /// The command gate keeps the strict session-exists form -- commands
    /// have no unlifted session to race -- which the drag wiring facts
    /// pin as the deliberate asymmetry.
    /// </remarks>
    [Fact]
    public void TheToggle_StandsDownUnderDrag_Fences_AndRoutesThroughTheManager()
    {
        var toggle = Strip().Method("ToggleGroup");

        var standDown = toggle.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Equal("_drag is { Machine.Phase: TabDragPhase.Dragging }",
            standDown.Condition.ToString());
        Assert.Contains(
            standDown.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
                .Select(r => r.ToString()),
            s => s == "return;");

        var collapse = toggle.Call("_manager.CollapseGroup");
        Assert.Equal("group", collapse.Arg(0));
        Assert.Equal("!group.IsCollapsed", collapse.Arg(1));

        var fence = toggle.AssignsTo("_syncing").Where(a => a.Right.ToString() == "true").ToList();
        Assert.Single(fence);
        Assert.True(fence[0].Span.Start < collapse.Span.Start,
            "the toggle must be fenced like every other path that ends in a " +
            "reconcile plus a selection sync");
    }

    /// <summary>
    /// Both gestures land on the one toggle, by different roads: the
    /// pointer chevron toggles directly (the user is watching it land), the
    /// keyboard claims the key BEFORE MUXC's list sees it -- or the same
    /// Enter also arrives as ItemInvoked and toggles twice -- and routes
    /// through the router as a command that announces.
    /// </summary>
    [Fact]
    public void BothGestures_FeedTheOneToggle_AndTheKeyboardClaimsTheKeyFirst()
    {
        var strip = Strip();

        var invoked = strip.Method("OnNavItemInvoked");
        var guard = invoked.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("InvokedItemContainer"));
        Assert.Contains(
            "args.InvokedItemContainer is not NavigationViewItem { Tag: TabGroup group }",
            guard.Condition.ToString());
        Assert.Contains(
            guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
                .Select(r => r.ToString()),
            s => s == "return;");
        Assert.Equal("group", invoked.Calls("ToggleGroup").Single().Arg(0));

        var key = strip.Method("OnGroupHeaderKeyDown");
        var keys = key.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Contains("Windows.System.VirtualKey.Enter", keys.Condition.ToString());
        Assert.Contains("Windows.System.VirtualKey.Space", keys.Condition.ToString());

        var handled = Assign(key, "e.Handled");
        Assert.Equal("true", handled.Right.ToString());
        Assert.True(handled.Span.Start < key.Calls("GroupToggleFromCommandRequested?.Invoke").Single().Span.Start,
            "the key must be claimed before the route, or MUXC re-fires it as " +
            "ItemInvoked and collapses the group twice");
        Assert.Empty(key.Calls("ToggleGroup"));

        // The command comes back to the one toggle: same-state guard, focus
        // re-home under the folding group, then the chevron's own toggle.
        var command = strip.Method("ToggleGroupFromCommand");
        Assert.Equal("_drag is not null",
            command.DescendantNodes().OfType<IfStatementSyntax>().First().Condition.ToString());
        Assert.Equal("group.IsCollapsed == collapsed",
            command.DescendantNodes().OfType<IfStatementSyntax>().ToList()[1].Condition.ToString());
    }

    /// <summary>
    /// Membership has no manager event: TabModel.Group raising on the tab
    /// drives its row's chrome immediately and defers layout to a coalesced
    /// pass; the header binding watches the three properties its row
    /// renders, and removal disposes the subscription behind the MUXC
    /// fence -- one outliving its row keeps reconciling.
    /// </summary>
    [Fact]
    public void TheGroupBindings_WatchTheRightProperties_AndReconcileCoalesced()
    {
        var strip = Strip();

        var body = strip.Method("AddBodyRow");
        var tabBinding = body.Calls("AotBinding.Create")
            .Single(c => c.Arg(2).Contains("nameof(TabModel.Group)"));
        Assert.Contains("OnTabGroupStateChanged", tabBinding.Arg(1).ToString());

        var handler = strip.Method("OnTabGroupStateChanged");
        Assert.True(
            handler.Calls("ApplyItemTitleChrome").Single().Span.Start
                < handler.Calls("ScheduleReconcile").Single().Span.Start,
            "the row's own chrome is immediate; only the layout is deferred");

        var schedule = strip.Method("ScheduleReconcile");
        Assert.Equal("_reconcileScheduled",
            schedule.DescendantNodes().OfType<IfStatementSyntax>().First().Condition.ToString());
        var enqueue = schedule.Call("DispatcherQueue.TryEnqueue");
        Assert.Contains("ReconcileRowOrder", enqueue.ArgumentList.Arguments[1].ToString());
        Assert.Contains("SyncSelectionFromManager", enqueue.ArgumentList.Arguments[1].ToString());

        var headerBinding = strip.Method("AddGroupRow").Calls("AotBinding.Create").Single();
        Assert.Contains("nameof(TabGroup.IsCollapsed)", headerBinding.ArgumentList.ToString());
        Assert.Contains("nameof(TabGroup.Title)", headerBinding.ArgumentList.ToString());
        Assert.Contains("nameof(TabGroup.Color)", headerBinding.ArgumentList.ToString());
        Assert.Contains("ScheduleReconcile", headerBinding.Arg(1).ToString());

        var remove = strip.Method("RemoveGroupRow");
        var fence = remove.AssignsTo("_syncing").Where(a => a.Right.ToString() == "true").ToList();
        Assert.Single(fence);
        var menuRemove = remove.Call("NavView.MenuItems.Remove");
        Assert.True(fence[0].Span.Start < menuRemove.Span.Start,
            "the header's removal fences MUXC selection like every other one");
        Assert.True(remove.Calls("hooks.Dispose").Single().Span.Start > menuRemove.Span.Start,
            "the subscription dies with the row, not before it");
    }

    /// <summary>
    /// The 4b-2 seam the headers broke: the shelf crossing aims at the
    /// first TAB row. Headers are top-level items now (a bare FirstOrDefault
    /// selects a collapse affordance), and hidden collapsed members sit
    /// ahead of the visible one, so visibility is in the predicate too.
    /// </summary>
    [Fact]
    public void TheKeyboardCrossing_LandsOnAVisibleTabRow_NeverAHeader()
    {
        var strip = Strip();
        var resolver = strip.Method("FirstBodyItem");

        var body = resolver.DescendantNodes().OfType<LambdaExpressionSyntax>()
            .Single(l => l.ExpressionBody is not null).ExpressionBody!.ToString();
        Assert.Contains("i.Tag is TabModel", body);
        Assert.Contains("i.Visibility == Visibility.Visible", body);
        Assert.Contains("FirstOrDefault", resolver.ExpressionBody!.ToString());

        Assert.Single(strip.Method("FocusShelfNeighbour").Calls("FirstBodyItem"));
        Assert.Single(strip.Method("OnBodyRowKeyDown").Calls("FirstBodyItem"));
    }

    /// <summary>
    /// The row renders the group in one Refresh; the chevron's arms are
    /// matched through the parsed literals' decoded values, so the polarity
    /// is the pin; the ink pass recolors text only -- the swatch is the
    /// group's content, not chrome.
    /// </summary>
    [Fact]
    public void TheHeaderRow_RendersTheGroup_AndKeepsItsSwatchOutOfTheInk()
    {
        var refresh = Header().Method("Refresh");
        Assert.Contains("_title.Text = group.Title", refresh.ToString());
        Assert.Contains("_count.Text = memberCount.ToString()", refresh.ToString());
        Assert.Contains("TabColorPalette.Background(group.Color, selected: false)",
            refresh.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.CalleeText() == "TabColorPalette.Background").ToString());
        Assert.Equal("group.IsCollapsed", refresh.Calls("SetChevron").Single().Arg(0));

        // Collapsed points right (E76C), expanded down (E70D). Matched in
        // raw source, where the escapes are plain ASCII text -- no decoded
        // literal can be mangled in here, and flipping either arm fails.
        Assert.Contains("collapsed ? \"\\uE76C\" : \"\\uE70D\"",
            Header().Method("SetChevron").ExpressionBody!.ToString());

        var ink = Header().Method("ApplyInk");
        Assert.Equal(
            new[] { "_title.Foreground", "_count.Foreground" },
            ink.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left.ToString()).ToList());
    }

    /// <summary>
    /// The one "these rows belong to the header above" cue: grouped members
    /// indent, ungrouped rows do not (inverting the ternary indents exactly
    /// the rows with no header). Applied at build AND on the reconcile, or
    /// a membership op leaves stale indents.
    /// </summary>
    [Fact]
    public void GroupedRows_Indent_AndUngroupedRowsDoNot()
    {
        var strip = Strip();
        var ternary = Assert.IsType<ConditionalExpressionSyntax>(
            Assign(strip.Method("ApplyGroupInset"), "row.Margin").Right);
        Assert.Equal("tab.Group is null", ternary.Condition.ToString());
        Assert.Equal("default(Thickness)", ternary.WhenTrue.ToString());
        Assert.Contains("GroupInsetLeft", ternary.WhenFalse.ToString());

        Assert.Single(strip.Method("AddBodyRow").Calls("ApplyGroupInset"));
        Assert.Single(strip.Method("ReconcileRowOrder").Calls("ApplyGroupInset"));
    }

    /// <summary>
    /// The machine's index for a row is its SLOT, and DragSlots' pairing
    /// runs slot -> manager, so resolving a row needs the inverse. Indexing
    /// the pairing BY the manager index answers another row's slot -- past
    /// a hidden run it is out of range (a plain press throws) or mis-seeds.
    /// </summary>
    [Fact]
    public void SlotIndexOf_ResolvesTheInversePairing_NeverTheForwardList()
    {
        var strip = Strip();
        var method = strip.Method("SlotIndexOf");
        var ret = method.DescendantNodes().OfType<ReturnStatementSyntax>().Single();
        Assert.Equal("manager >= 0 ? managerIndex.IndexOf(manager) : -1",
            ret.Expression!.ToString());
        Assert.DoesNotContain("managerIndex[_manager.IndexOf", method.ToString());

        // The three machine-index sites (press, zone churn, drag tick) all
        // resolve through the one inverse helper.
        Assert.Equal(3, strip.Root.Calls("SlotIndexOf").Count());
    }

    /// <summary>
    /// Collapse hides rows in place, so no churn hand-off saves focus: the
    /// pointer toggle re-homes it on the header -- but only when the group
    /// folding up is the one holding focus, and only on the collapse arm
    /// (expand hides nothing).
    /// </summary>
    [Fact]
    public void ThePointerToggle_RehomesFocus_OnlyWhenTheFoldingGroupHoldsIt()
    {
        var strip = Strip();
        var invoked = strip.Method("OnNavItemInvoked");
        var gate = invoked.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!group.IsCollapsed");
        Assert.True(gate.Span.Start < invoked.Calls("ToggleGroup").Single().Span.Start,
            "the focus read must precede the toggle that hides the row");

        var restore = strip.Method("RestoreFocusUnder");
        var conditions = restore.DescendantNodes().OfType<IfStatementSyntax>()
            .Select(i => i.Condition.ToString()).ToList();
        Assert.Contains(conditions,
            c => c.Contains("is not NavigationViewItem { Tag: TabModel focused }"));
        Assert.Contains(conditions,
            c => c == "!ReferenceEquals(focused.Group, group)");

        Assert.Equal("FocusState.Programmatic",
            restore.Calls("header.Focus").Single().Arg(0));
        // The shared toggle stays focus-free: only the pointer path repairs.
        Assert.Empty(strip.Method("ToggleGroup").Calls("RestoreFocusUnder"));
    }

    /// <summary>
    /// The header's UIA ExpandCollapse pattern is the keyboard route in a
    /// screen reader's hands. The item carries the toggle event and raises
    /// it only from the pattern -- never from pointer, which toggles in
    /// place -- and the strip forwards it to the command event the key
    /// handler uses, which is the whole reason the pattern announces. The
    /// peer mirrors the keyboard polarity (Expand asks for expanded) and
    /// reads its state live off the group.
    /// </summary>
    [Fact]
    public void TheUIAPattern_FeedsTheCommandEvent_WithTheKeyboardPolarity()
    {
        var add = Strip().Method("AddGroupRow");
        var wiring = add.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "item.GroupToggleRequested");
        Assert.Contains("GroupToggleFromCommandRequested?.Invoke", wiring.Right.ToString());

        var item = ShellSource.Load("Tabs.VerticalTabGroupHeaderItem.cs");
        // One raiser, and it is pattern-only: nothing else in the item may
        // toggle the group on its own.
        Assert.Single(item.Root.Calls("GroupToggleRequested?.Invoke"));
        var raiser = item.Method("RaiseGroupToggleFromPattern");
        var guard = Assert.IsType<IfStatementSyntax>(raiser.Body!.Statements.Single());
        Assert.Equal("Tag is TabGroup group", guard.Condition.ToString());
        Assert.Contains("VerticalTabGroupHeaderItemAutomationPeer",
            item.Method("OnCreateAutomationPeer").ExpressionBody!.ToString());

        var peer = ShellSource.Load(
            "Accessibility.VerticalTabGroupHeaderItemAutomationPeer.cs");
        var pattern = Assert.IsType<ConditionalExpressionSyntax>(
            peer.Method("GetPatternCore").ExpressionBody!.Expression);
        Assert.Equal("patternInterface == PatternInterface.ExpandCollapse",
            pattern.Condition.ToString());
        Assert.Equal("this", pattern.WhenTrue.ToString());

        var state = peer.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ExpandCollapseState");
        var polarity = Assert.IsType<ConditionalExpressionSyntax>(
            state.ExpressionBody!.Expression);
        Assert.Equal("ExpandCollapseState.Collapsed", polarity.WhenTrue.ToString());
        Assert.Equal("ExpandCollapseState.Expanded", polarity.WhenFalse.ToString());

        Assert.Contains("AutomationControlType.ListItem",
            peer.Method("GetAutomationControlTypeCore").ExpressionBody!.ToString());
        Assert.Contains("collapsed: false",
            peer.Method("Expand").ExpressionBody!.ToString());
        Assert.Contains("collapsed: true",
            peer.Method("Collapse").ExpressionBody!.ToString());
    }
}
