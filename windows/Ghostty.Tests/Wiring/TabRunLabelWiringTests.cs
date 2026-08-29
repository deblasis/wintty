using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The group run label's wiring: the drag-start cut, the sugar-only
/// surface, the rail's chrome path, and the motion gate. The shell cannot
/// load into this test host, so these parse it; the rules themselves are
/// tested outright in TabRunLabelShapeTests.
/// </summary>
public sealed class TabRunLabelWiringTests
{
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";
    private const string LabelSource = "Tabs.TabRunLabel.cs";
    private const string VerticalSource = "Tabs.VerticalTabStrip.xaml.cs";
    private const string VerticalHostSource = "Tabs.VerticalTabHost.xaml.cs";
    private const string MainWindowSource = "MainWindow.xaml.cs";

    [Fact]
    public void The_drag_start_hides_the_label_in_its_own_dispatch_pass()
    {
        var dragStart = ShellSource.Load(TabHostSource).Method("OnTabDragStarting");

        // The hide is the machine's drag rule applied in this handler's
        // own body: not a timer arm, not a deferred close. A timer between
        // the drag start and the hide is the exact overlap the rule
        // forbids, so its absence is asserted, not assumed.
        var cut = dragStart.InvocationWithArgument("_labelRules.DragStarting");
        Assert.True(
            cut is not null,
            "OnTabDragStarting must apply the label rule machine's drag start.");
        var seamRaise = dragStart.Calls("SelectedTabSeamChanged?.Invoke")
            .Select(c => c.SpanStart)
            .DefaultIfEmpty(-1)
            .Min();
        Assert.True(
            cut.SpanStart < seamRaise,
            "the label hide must precede the drag's own teardown raise: " +
            "the drag is live from that call onward.");
        Assert.Empty(dragStart.Calls("_labelShowTimer.Start"));
        Assert.Empty(dragStart.Calls("_labelGraceTimer.Start"));

        // The drag's end lifts the cut demand; the label stays hidden and
        // hover may show it again. Without this the next ordinary hide
        // would still read the drag's cut.
        var dragEnd = ShellSource.Load(TabHostSource).Method("OnTabDragCompleted");
        Assert.True(
            dragEnd.InvocationWithArgument("_labelRules.DragEnded") is not null,
            "OnTabDragCompleted must end the label's drag state.");

        // The cross-host half: the vertical strip raises its drag-live
        // moment, and the window closes the horizontal label with it --
        // one dispatch pass, a different strip.
        var vertical = ShellSource.Load(VerticalSource).Method("StartDragVisual");
        Assert.True(
            vertical.Calls("DragVisualStarted?.Invoke").Count == 1,
            "StartDragVisual must raise DragVisualStarted in the pass that " +
            "goes live.");
        var window = ShellSource.Load(MainWindowSource).Root;
        Assert.Contains(window.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_verticalTabHost.DragVisualStarted"
                && a.Right.ToString().Contains("CloseRunLabelForDrag"));
    }

    [Fact]
    public void The_drag_end_lifts_the_refusal_through_the_same_pair()
    {
        // The drag-start fact pins the start pair; this is the end pair,
        // and a suite that pins a start raise but not its end is exactly
        // how a missing off-switch ships: DragLive with no way down means
        // ONE vertical drag silences the label for the session -- hover,
        // keyboard, all refused, the cut demand stuck on.
        var vertical = ShellSource.Load(VerticalSource);
        var endDrag = vertical.Method("EndDrag");

        // Raised once, from the funnel. Every exit -- drop, drop with
        // nothing committed, cancel, strip teardown -- passes through
        // EndDrag, so one raise inside it covers them all; a raise
        // anywhere else is a second door a narrower exit can skip.
        Assert.True(
            endDrag.Calls("DragVisualEnded?.Invoke").Count == 1,
            "EndDrag must raise the drag's end for the horizontal label.");
        var stray = vertical.Root.Calls("DragVisualEnded?.Invoke")
            .Where(i => !endDrag.FullSpan.Contains(i.Span))
            .ToList();
        Assert.Empty(stray);

        // The cancel exit explicitly rides the funnel, so a cancelled or
        // torn-down drag lifts the refusal too.
        Assert.True(
            vertical.Method("CancelDrag").Calls("EndDrag").Count == 1,
            "the cancel path must end the drag through the funnel.");

        // And the window forwards the raise to the horizontal strip's
        // seam, whose body is the machine's DragEnded -- the refusal
        // lifts and hover may show again.
        var window = ShellSource.Load(MainWindowSource).Root;
        Assert.Contains(window.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_verticalTabHost.DragVisualEnded"
                && a.Right.ToString().Contains("EndRunLabelDrag"));
        var seam = ShellSource.Load(TabHostSource).Method("EndRunLabelDrag");
        Assert.True(
            seam.InvocationWithArgument("_labelRules.DragEnded") is not null,
            "the end seam must apply the machine's drag-ended rule.");

        // The chain's middle link is the host's passthrough, and it is
        // the one link every other assertion here skips over: the
        // window's subscription compiles against the event's NAME, so an
        // accessor that swallows its value -- `add => { }` -- compiles,
        // parses, and leaves the raise and the seam green while the
        // chain carries nothing. The bodies are pinned, not the shape:
        // both accessors must actually reach the strip's event.
        var passthrough = ShellSource.Load(VerticalHostSource).Root
            .DescendantNodes().OfType<EventDeclarationSyntax>()
            .First(e => e.Identifier.ValueText == "DragVisualEnded");
        var add = passthrough.AccessorList!.Accessors
            .First(a => a.Keyword.ValueText == "add");
        var remove = passthrough.AccessorList!.Accessors
            .First(a => a.Keyword.ValueText == "remove");
        Assert.Contains(
            "_strip.DragVisualEnded += value", add.ToString());
        Assert.Contains(
            "_strip.DragVisualEnded -= value", remove.ToString());
    }

    /// <summary>
    /// The start half of the pair carries the same swallowed-accessor
    /// hole as the end half: the window's subscription compiles against
    /// the event's name, so a no-op add accessor compiles clean, keeps
    /// every other link here green, and decapitates the chain -- the
    /// label would stay up across every vertical drag. Same pin as the
    /// end pair, same reason: the bodies, not the shape.
    /// </summary>
    [Fact]
    public void The_drag_start_passthrough_IsPairedLikeItsEnd()
    {
        var passthrough = ShellSource.Load(VerticalHostSource).Root
            .DescendantNodes().OfType<EventDeclarationSyntax>()
            .First(e => e.Identifier.ValueText == "DragVisualStarted");
        var add = passthrough.AccessorList!.Accessors
            .First(a => a.Keyword.ValueText == "add");
        var remove = passthrough.AccessorList!.Accessors
            .First(a => a.Keyword.ValueText == "remove");
        Assert.Contains(
            "_strip.DragVisualStarted += value", add.ToString());
        Assert.Contains(
            "_strip.DragVisualStarted -= value", remove.ToString());
    }

    [Fact]
    public void The_label_is_hit_test_sugar_only()
    {
        var label = ShellSource.Load(LabelSource).Root;
        var ctor = label.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .First(c => c.Identifier.ValueText == "TabRunLabel");

        // Non-focusable by construction: clicks pass through to the strip
        // beneath, the element never joins the focus chain, and every
        // click that lands on it therefore fires a hide rule -- the
        // light-dismiss behavior without any focus-holding surface.
        Assert.Contains(ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "IsHitTestVisible"
                && a.Right.ToString() == "false");

        // No automation surface and no focus calls anywhere in the file:
        // screen readers get the group title from the member items, never
        // from hover; that contract deliberately keeps the label out.
        Assert.Empty(label.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().Contains("AutomationProperties")));
        Assert.Empty(label.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith(".Focus")
                || c.CalleeText() == "Focus"));
    }

    [Fact]
    public void The_rail_is_member_chrome_painted_by_the_chrome_pass()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var chrome = tabHost.Method("ApplyTabChrome");

        // The rail is not a parallel paint path: it lives inside the same
        // per-item pass every header brush rides, so a join, a leave, or
        // a theme pass re-derives it for free. A paint outside this pass
        // is a second door that will be forgotten.
        var railRead = chrome.Calls("_railByModel.TryGetValue");
        Assert.True(
            railRead.Count == 1,
            $"ApplyTabChrome must read the rail exactly once; found {railRead.Count}.");
        // Nearest if first: the read is the gate itself. Grouped paints,
        // ungrouped collapses -- the fork sits inside the gate.
        var gate = railRead[0].Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Contains(
            "_railByModel.TryGetValue", gate.Condition.ToString());
        var fork = gate.Statement.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("tab.Group is"));
        Assert.True(fork is not null, "the rail's paint must fork on membership.");
        Assert.True(
            fork.Statement.ToString().Contains("TabColorPalette.Background")
                && fork.Statement.ToString().Contains("Visibility.Visible"),
            "a grouped tab paints the rail in the group's palette color.");
        Assert.Contains(
            "Visibility.Collapsed", fork.Else?.Statement.ToString() ?? string.Empty);

        // The rail element is built in the header's build loop and takes
        // the TOP slot: above the icon row, which is what puts it at the
        // top edge the label anchors four pixels above.
        var addItem = tabHost.Method("AddItem");
        var railBuild = addItem.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString().Contains("Rectangle"))
            .ToList();
        Assert.True(
            railBuild.Count == 1,
            $"AddItem builds exactly one rail rectangle; found {railBuild.Count}.");
        var adds = addItem.Calls("headerPanel.Children.Add");
        var railAdd = adds.FirstOrDefault(a => a.Arg(0) == "rail");
        var iconRowAdd = adds.FirstOrDefault(a => a.Arg(0) == "iconRow");
        Assert.True(
            railAdd is not null && iconRowAdd is not null
                && railAdd.SpanStart < iconRowAdd.SpanStart,
            "the rail must be added before the icon row: it owns the top slot.");
    }

    [Fact]
    public void A_group_color_change_repaints_the_run_through_one_door()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var groupChanged = tabHost.Method("OnGroupPropertyChanged");

        // Color and title fall through the collapse branch to the one
        // refresh; the members' chrome rides the same door the chip's
        // swatch rides. Deleting the run refresh here is how a run ends
        // up two-tone after a recolor.
        Assert.True(
            groupChanged.Calls("RefreshRunRails").Count == 1,
            "OnGroupPropertyChanged must refresh the run's member chrome.");

        // The collapse arm is also a label hide rule, from the machine.
        var collapse = groupChanged.InvocationWithArgument("_labelRules.Collapsed");
        Assert.True(
            collapse is not null,
            "a collapse must close the label through the rule machine.");

        var refresh = tabHost.Method("RefreshRunRails");
        Assert.True(
            refresh.Calls("_manager.MembersOf").Count == 1
                && refresh.Calls("ApplyTabChrome").Count == 1,
            "RefreshRunRails re-runs the per-item chrome pass over the run's members.");
    }

    [Fact]
    public void The_label_show_reads_the_run_from_identity_maps()
    {
        var show = ShellSource.Load(TabHostSource).Method("ShowRunLabel");

        // The run comes from the manager and the elements from the strip's
        // model map -- never from TabItems order, which hides members
        // behind chips and cannot say where a run starts.
        Assert.True(
            show.Calls("_manager.MembersOf").Count == 1,
            "the run's members come from the manager.");
        Assert.Equal(2, show.Calls("_itemByModel.TryGetValue").Count);
        Assert.True(
            show.Calls("_runLabel.ShowFor").Count == 1,
            "the show lands on the label element.");
        Assert.Empty(show.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("TabItems.IndexOf")));
    }

    [Fact]
    public void The_label_fades_by_the_motion_gate_and_cuts_for_a_drag()
    {
        var label = ShellSource.Load(LabelSource);
        var hide = label.Method("Hide");

        // A drag-start hide is zero duration -- a cut -- regardless of the
        // gate; every other hide reads the gate. Reading the gate for the
        // cut is the bug: an 83ms fade over a lifting ghost is precisely
        // the overlap the rule exists to forbid.
        var cut = hide.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Where(c => c.Condition.ToString() == "cut")
            .ToList();
        Assert.True(
            cut.Count == 1,
            $"Hide must branch on the cut flag exactly once; found {cut.Count}.");
        Assert.Equal("TimeSpan.Zero", cut[0].WhenTrue.ToString());
        Assert.Contains("TabRunLabelShape.FadeDuration", cut[0].WhenFalse.ToString());

        // The show reads the gate too, through the machine's shape -- not
        // its own constant.
        var showFor = label.Method("ShowFor");
        Assert.True(
            showFor.Calls("TabRunLabelShape.FadeDuration").Count == 1,
            "the show's duration comes from the shared shape.");

        // The cut is landed in the same pass: a zero duration writes the
        // end state directly instead of waiting for a storyboard tick.
        var runFade = label.Method("RunFade");
        var zero = runFade.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString() == "duration == TimeSpan.Zero");
        Assert.True(
            zero is not null && zero.Statement.AssignsTo("Opacity").Any(),
            "a zero-duration fade must write the end state in the same pass.");
    }

    [Fact]
    public void The_window_supplies_the_motion_gate_and_hosts_the_label()
    {
        var window = ShellSource.Load(MainWindowSource).Root;

        // The label is hosted on the morph layer's canvas -- the surface
        // both strips are measured in -- and its gate is the strips' gate:
        // TabStripMotion.Enabled over the OS animation read and the
        // composed High Contrast state, not raw IsActive.
        Assert.Contains(window.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_runLabel.MotionEnabled"
                && a.Right.ToString().Contains("TabStripMotion.Enabled")
                && a.Right.ToString().Contains("HighContrastChromeActive"));
        Assert.Contains(window.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "TabMorphLayer.Children.Add");
        Assert.Contains(window.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "_horizontalTabHost.AttachRunLabel");

        // Deactivation is a window-side hide rule, and it goes through the
        // strip's machine door: a bare element hide would leave the phase
        // pending and its timers armed, and the label would surface again
        // on a window nobody is looking at. Both ends pinned -- the
        // window subscribes, and the seam applies the machine's rule,
        // which lands Idle and cancels whatever was pending.
        var deactivate = window.DescendantNodes()
            .Where(n => n is InvocationExpressionSyntax i
                && i.CalleeText() == "_horizontalTabHost.CloseRunLabelForDeactivation")
            .ToList();
        Assert.True(
            deactivate.Count == 1,
            "the window hides the label exactly once: on deactivation, " +
            "through the strip's machine door.");
        var deactivationSeam = ShellSource.Load(TabHostSource)
            .Method("CloseRunLabelForDeactivation");
        Assert.True(
            deactivationSeam.InvocationWithArgument("_labelRules.Deactivated")
                is not null,
            "the deactivation seam must apply the machine's rule, not a " +
            "bare element hide.");

        // So is a layout switch request: ToggleTabLayout is where every
        // chord, menu item, and settings toggle lands, and the label is
        // anchored to the strip the switch is about to replace.
        var toggle = ShellSource.Load(MainWindowSource).Method("ToggleTabLayout");
        Assert.True(
            toggle.Calls("_horizontalTabHost.CloseRunLabelForLayoutSwitch").Count == 1,
            "a layout switch request must close the label through the strip seam.");
    }
}

internal static class TabRunLabelWiringQueries
{
    /// <summary>
    /// The one invocation in <paramref name="method"/> whose argument list
    /// contains a call to <paramref name="callee"/>, or null. For the
    /// ApplyLabelPhase translations: the rule call is the argument, and
    /// the argument is the polarity the wiring fact names.
    /// </summary>
    public static InvocationExpressionSyntax? InvocationWithArgument(
        this MethodDeclarationSyntax method, string callee)
        => method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.ArgumentList.Arguments.Any(
                a => a.Expression.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(inner => inner.CalleeText() == callee)));
}
