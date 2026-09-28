using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pane fades' wiring. PaneHost is a WinUI type no test host can
/// construct, so what a test can reach is its source; these parse it.
/// The behavioural half -- the flight law, the settle write, the gate's
/// answers -- lives in Motion.PaneFadeTrackerTests and the seam's own
/// suites; the census pins the registry routing. What is only observable
/// here is that the pieces stay bolted together the way the fades were
/// wired: the split fades only its new leaf and asks the fade-bearing
/// gate (not the strip's movement route), the soft close fades and the
/// hard close does not, every tree operation settles the fades before it
/// reads the tree, and equalize still ASKS the gate without arming a
/// fade -- so a moved, unguarded or unrouted fade reds somewhere
/// specific instead of silently becoming a plain cut, or worse, an
/// ungated one.
/// </summary>
public class PaneFadeWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Panes.PaneHost.cs");

    private static ShellSource Fades() => ShellSource.Load("Panes.PaneFades.cs");

    private static MethodDeclarationSyntax SplitOverload() =>
        Host().Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Split"
                         && m.ParameterList.Parameters.Count == 2);

    private static MethodDeclarationSyntax CloseOverload() =>
        Host().Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CloseLeaf"
                         && m.ParameterList.Parameters.Count == 2);

    private static IfStatementSyntax SoftCloseBranch(MethodDeclarationSyntax close) =>
        close.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "softClose");

    // -- The split's fade-in ---------------------------------------------------

    /// <summary>
    /// The split's geometry snaps: the splice above the fade site is the
    /// whole movement story, and the fade rides only the new pane. Off
    /// (gate down) arms nothing -- the pane mounts at full strength, the
    /// plain cut.
    /// </summary>
    [Fact]
    public void The_split_fades_only_its_new_leaf_at_the_fade_gate()
    {
        var split = SplitOverload();

        var fade = Assert.Single(split.Calls("StartPaneFade"));
        Assert.Equal("newLeaf", fade.Arg(0));

        // The kind is a ternary ON the gate read, not a bare kind: the
        // arming consults the fade-bearing gate at the site itself, so a
        // refactor that hoists the read behind an unrelated condition
        // still has to name the gate here.
        var kind = Assert.IsType<ConditionalExpressionSyntax>(fade.ArgExpression(1));
        Assert.Equal("PaneFadeKind.SplitIn", kind.WhenTrue.ToString());
        Assert.Equal("PaneFadeKind.None", kind.WhenFalse.ToString());
        kind.Condition.AssertCallTo("SystemAnimations.Enabled");
        Assert.Contains(
            "MotionSurfaceClass.PaneGeometry",
            kind.Condition.ToString(),
            StringComparison.Ordinal);

        // The geometry snaps first: the fade site stands after the splice
        // if/else (the statement whose condition names the in-place
        // splice), so the pane arrives in place and only its opacity
        // animates. No movement animation exists here.
        var splice = Assert.Single(split.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("newSubSplit is null"));
        Assert.True(
            splice.Span.End < fade.Span.Start,
            "the split's fade must arm after the splice if/else: the geometry "
            + "snaps and only the new pane's opacity fades");
    }

    /// <summary>
    /// A pane is not the strip: the fade asks the fade-bearing route the
    /// bell fade and the strip's fades ask, never the movement route the
    /// strip's slides ride. Stated file-wide, because a pane surface
    /// asking TabStripMotion is wrong wherever it appears.
    /// </summary>
    [Fact]
    public void The_pane_fades_ask_the_fade_gate_never_the_movement_route()
    {
        Assert.Empty(Host().Root.Calls("TabStripMotion.Enabled"));
    }

    // -- The soft close's fade-away ---------------------------------------------

    /// <summary>
    /// Only the soft close fades: the branch that
    /// retains the shell for undo arms a fade-away and DEFERS the visual
    /// cut behind it; the hard path (shell-exit, undo off, last pane)
    /// takes the plain cut exactly as before. The fade arms before the
    /// pending slot is stored, so there is never a pending close whose
    /// fade was never started.
    /// </summary>
    [Fact]
    public void The_soft_close_fades_away_and_the_hard_close_does_not()
    {
        var close = CloseOverload();
        var soft = SoftCloseBranch(close);
        var retained = Assert.IsType<BlockSyntax>(soft.Statement);

        var fade = Assert.Single(retained.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "StartPaneFade");
        Assert.Equal("leaf", fade.Arg(0));
        Assert.Equal("PaneFadeKind.SoftCloseOut", fade.Arg(1));

        // The gate ask sits in the soft branch: gate down, the soft close
        // takes the same plain cut the hard path takes.
        var gate = Assert.Single(retained.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "SystemAnimations.Enabled");
        Assert.Contains("MotionSurfaceClass.PaneGeometry", gate.Arg(0), StringComparison.Ordinal);

        // The arm precedes the pending store: no slot without a fade.
        var pending = Assert.Single(retained.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_pendingSoftClose");
        Assert.True(
            fade.Span.Start < pending.Span.Start,
            "the fade must arm before the pending close is stored");

        // And the hard path stays bare: the one fade in CloseLeaf is the
        // soft branch's.
        Assert.Single(close.Calls("StartPaneFade"));
    }

    /// <summary>
    /// Both paths run the same cut. The deferred tail is the whole
    /// method: detach the leaf, collapse the split, splice the sibling,
    /// run the notifications. A gate-off soft close and every hard close
    /// reach it inline; a gated-on soft close reaches it from the fade's
    /// Completed or the settle that ends the fade early.
    /// </summary>
    [Fact]
    public void FinishClose_carries_the_cut_both_paths_run()
    {
        var close = CloseOverload();
        Assert.Single(close.Calls("FinishClose"));

        var finish = Host().Method("FinishClose");

        // The visual cut leads: the leaf leaves the visual tree before
        // anything else in the tail runs, exactly where it sat before the
        // deferral.
        var statements = finish.Body!.Statements;
        Assert.True(statements.Count > 2, "FinishClose should carry the deferred tail");
        Assert.Contains(
            statements[0].DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "DetachFromParent");

        // The last-leaf early return keeps its flag-and-notify shape.
        Assert.Contains(finish.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "LastLeafClosed?.Invoke");

        // The tail still splices (the captured parent Grid flows into it)
        // and still raises.
        var splice = Assert.Single(finish.Calls("TryIncrementalCloseRebuild"));
        Assert.Equal("leafParentGrid", splice.Arg(0));
        Assert.Contains(finish.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "RaiseLayoutChanged");

        // A completion never starts a fade: FinishClose finishes things.
        Assert.Empty(finish.Calls("StartPaneFade"));
        Assert.Empty(finish.Calls("AnimationActivityRegistry.BeginStoryboard"));
    }

    // -- The stale Completed -----------------------------------------------------

    /// <summary>
    /// A Completed is only ever its own board's. WinUI 3 raises Completed
    /// from Stop, so a settle that stops a board can deliver its
    /// Completed after a replacement fade has already been armed on the
    /// same leaf -- a split, then a soft close of that split's new pane
    /// inside the fade window. The handler the split armed must stand
    /// down on the board's identity: an arrival that cannot prove it IS
    /// the board currently stored returns before it touches the
    /// dictionary, the tracker or the pending close. Without that guard
    /// the stale arrival deletes the replacement's entry, clears its
    /// flight, and the deferred cut is never run -- the same shape
    /// VerticalTabStrip's field glides guard their handler against. The
    /// subscription therefore hands the board to the handler.
    /// </summary>
    [Fact]
    public void A_stale_completed_cannot_touch_a_replacement_fade()
    {
        var host = Host();

        // The subscription passes its own board in: the handler has
        // something to prove its identity against.
        var start = host.Method("StartPaneFade");
        var raise = Assert.Single(start.DescendantNodes()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "OnPaneFadeCompleted");
        Assert.True(raise.ArgumentList.Arguments.Count == 3,
            "StartPaneFade must hand its board to OnPaneFadeCompleted: without " +
            "it a stale Completed from a settle-stopped board cannot be told " +
            "apart from the replacement's own");
        Assert.Equal("board", raise.Arg(2));

        // The handler opens with the identity guard, before ANY state
        // changes: a board that is not the one standing returns here.
        var handler = host.Method("OnPaneFadeCompleted");
        var guard = Assert.IsType<IfStatementSyntax>(
            handler.Body!.Statements.First());
        // The condition is written across two source lines; compare its
        // words, not the wrap.
        var condition = string.Join(" ",
            guard.Condition.ToString().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            "!_paneFadeBoards.TryGetValue(leaf, out var current) " +
            "|| !ReferenceEquals(current.Board, board)",
            condition);
        Assert.Contains(
            guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
            r => r.Expression is null);

        // The guard is not a replacement for the bookkeeping: the live
        // board's arrival still retires the entry and still runs the
        // soft close's cut.
        Assert.Single(handler.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "_paneFadeBoards.Remove");
        Assert.Contains(handler.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "FinishClose");
    }

    // -- The rebuild law -------------------------------------------------------

    /// <summary>
    /// Any rebuild or rehost of a mid-fade pane completes the fade and
    /// resets Opacity to 1 BEFORE Rebuild() runs. Pinned at the head of
    /// every tree operation: Rebuild itself, the split, the close (before
    /// its retained-leaf guard, because settling can complete a pending
    /// close of the same leaf), the zoom toggle, the rehost, and
    /// RestoreFrom -- which reassigns the tree the pending close closes
    /// against, so it must settle before that assignment and cannot rely
    /// on Rebuild's own head. The dispose sweep takes the same settle but
    /// drops the pending close: a window going away does not owe a tab
    /// its deferred cut.
    /// </summary>
    [Fact]
    public void Every_tree_operation_settles_the_fades_first()
    {
        var host = Host();

        Assert.Equal(8, host.Root.Calls("SettlePaneFades").Count);

        // Rebuild: the law IS the first statement -- "before Rebuild()"
        // stated against Rebuild itself.
        AssertFirstStatement(host.Method("Rebuild"), "Rebuild");

        // Split, the zoom toggle, and the rehost: same head, before any of
        // them reads the tree.
        AssertFirstStatement(SplitOverload(), "Split");
        AssertFirstStatement(host.Method("ToggleSplitZoom"), "ToggleSplitZoom");
        AssertFirstStatement(host.Method("RehostTo"), "RehostTo");

        // Restore: after its null guard (an undo with an empty history
        // settles nothing) but BEFORE the snapshot reassigns the tree the
        // pending close closes against -- that ordering is the load-bearing
        // one, and Rebuild's own head cannot carry it for this caller.
        var restore = host.Method("RestoreFrom");
        var restoreSettle = Assert.Single(restore.Calls("SettlePaneFades"));
        var rootAssign = Assert.Single(restore.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_root");
        Assert.True(
            restoreSettle.Span.Start < rootAssign.Span.Start,
            "RestoreFrom must settle the fades before the snapshot reassigns _root");

        // Close: before the retained-leaf guard (the settle can complete
        // a pending close of the same leaf, and the guard must see the
        // tree as it then stands), and so before the model close.
        var close = CloseOverload();
        var settle = Assert.Single(close.Calls("SettlePaneFades"));
        var guard = Assert.Single(close.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("Leaves(_root).Contains(leaf)"));
        var modelClose = Assert.Single(close.Calls("PaneTree.Close"));
        Assert.True(
            settle.Span.Start < guard.Span.Start && guard.Span.End < modelClose.Span.Start,
            "the close must settle the fades before its guard and "
            + "PaneTree.Close read the tree");

        // Dispose: settles but drops the pending close -- the tab is going
        // away, so a deferred soft cut must not run against a dying host.
        var sweep = host.Method("DisposeAllLeaves");
        var drop = Assert.Single(sweep.Calls("SettlePaneFades"));
        Assert.Contains("false", drop.Arg(0), StringComparison.Ordinal);
    }

    private static void AssertFirstStatement(MethodDeclarationSyntax method, string name)
    {
        var first = method.Body!.Statements[0];
        var settle = Assert.Single(
            first.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "SettlePaneFades");
        Assert.True(
            first.Span.Start == settle.Span.Start,
            $"{name}'s first statement must be the fade settle");
    }

    /// <summary>
    /// The restore write is the settle's substance: opacity back to 1 --
    /// full strength, not the mid-fade value a rebuild would inherit --
    /// and the soft close's pane takes hits again. StartPaneFade routes
    /// its one board through the registry (the census row pins the count;
    /// this pins the property) and arms nothing on None.
    /// </summary>
    [Fact]
    public void The_settle_write_restores_the_control_and_the_fade_routes_through_the_registry()
    {
        var host = Host();

        var restore = host.Method("RestoreFadeTarget");
        var opacity = Assert.Single(restore.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "t.Opacity");
        Assert.Equal("1", opacity.Right.ToString());

        var hitTest = Assert.Single(restore.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "t.IsHitTestVisible");
        var hitGuard = hitTest.Ancestors().OfType<IfStatementSyntax>()
            .First(i => i.Condition.ToString().Contains("SoftCloseOut"));
        Assert.Contains(hitTest, hitGuard.DescendantNodes());

        var start = host.Method("StartPaneFade");

        // None arms nothing: the plain cut is a structural return, not a
        // board that happens to span zero time.
        var none = Assert.IsType<IfStatementSyntax>(start.Body!.Statements[0]);
        Assert.Equal("kind == PaneFadeKind.None", none.Condition.ToString());

        // One begin, refused when a flight stands (one fade per pane).
        var begin = Assert.Single(start.Calls("PaneFadeTracker.Begin"));
        Assert.True(
            begin.Ancestors().OfType<IfStatementSyntax>().First().Condition.ToString()
                .StartsWith("!", StringComparison.Ordinal),
            "the begin guard must refuse a second flight");

        // The routing: the registry performs the start, on Opacity.
        var routed = Assert.Single(start.Calls("AnimationActivityRegistry.BeginStoryboard"));
        Assert.Equal("\"Opacity\"", routed.Arg(2));
    }

    // -- Equalize asks the gate ------------------------------------------------

    /// <summary>
    /// The equalize path ASKS the gate and changes
    /// nothing at Full or Reduced -- the end state is fully legible, no
    /// equalize fade exists, and the instant re-apply is the answer at
    /// every level that allows it. The Off answer still cuts what an Off
    /// gate cuts anywhere: a fade standing on a pane is settled before
    /// the ratios move.
    /// </summary>
    [Fact]
    public void Equalize_asks_the_gate_and_stays_instant()
    {
        var equalize = Host().Method("EqualizeSplits");

        var gate = Assert.Single(equalize.Calls("SystemAnimations.Enabled"));
        Assert.Contains("MotionSurfaceClass.PaneGeometry", gate.Arg(0), StringComparison.Ordinal);

        // The cut branch settles: the ask's one consumer.
        var ask = Assert.Single(equalize.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("SystemAnimations.Enabled"));
        Assert.Contains(
            ask.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "SettlePaneFades");

        // Instant behavior is untouched: the model equalizes and the
        // ratios re-apply in place, exactly as before this wiring.
        Assert.Single(equalize.Calls("PaneTree.Equalize"));
        Assert.Single(equalize.Calls("ApplyAllRatios"));

        // And the equalize path arms nothing: no fade starts, no board
        // is built.
        Assert.Empty(equalize.Calls("StartPaneFade"));
        Assert.Empty(equalize.Calls("AnimationActivityRegistry.BeginStoryboard"));
    }

    // -- The cap -----------------------------------------------------------------

    /// <summary>
    /// Every pane fade runs no longer than the fades' cap
    /// (120ms), and the boards take their duration from that one
    /// constant: there is no second duration a refactor could grow.
    /// </summary>
    [Fact]
    public void The_fade_cap_is_the_only_duration_a_board_builds()
    {
        var fades = Fades();

        var build = fades.Method("BuildBoard");
        var duration = Assert.Single(
            fades.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().EndsWith("FromMilliseconds", StringComparison.Ordinal));
        Assert.Contains("PaneFadeTracker.FadeMs", duration.Arg(0), StringComparison.Ordinal);

        // The board animates Opacity and nothing else, and the two kinds
        // are both spoken for (fade in, fade away).
        var property = Assert.Single(build.Calls("Storyboard.SetTargetProperty"));
        Assert.Equal("\"Opacity\"", property.Arg(1));
        var file = build.Body!.ToString();
        Assert.Contains("PaneFadeKind.SplitIn", file, StringComparison.Ordinal);
        Assert.Contains("PaneFadeKind.SoftCloseOut", file, StringComparison.Ordinal);
    }
}
