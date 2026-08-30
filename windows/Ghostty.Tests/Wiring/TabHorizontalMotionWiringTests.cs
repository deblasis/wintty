using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal strip's motion: the drag lift and the collapse swap's
/// appear-hand. The capture-less engine owns the drag itself -- the arm,
/// the crossings, the release -- so what is pinned here is the polish
/// the host adds around it: the pressed tab's own visual lifts at the
/// threshold and settles back on the release, a shadow carries the
/// depth, and the chip &lt;-&gt; members swap fades what appears while
/// removals stay immediate.
///
/// Two properties are pinned. The motion gate is the first statement of
/// everything that animates, and its cut is total: motion off means no
/// composition work at all, not different composition work -- state
/// correctness never waits on an animation. And every animation is
/// programmatic: the machine's velocity is not spent on any settle yet,
/// so both springs start at rest, the policy the vertical's pin flight
/// runs on.
///
/// Wiring guards, not behaviour tests: what a lift looks like on screen
/// is only observable on a live strip.
/// </summary>
public class TabHorizontalMotionWiringTests
{
    private const string HostSource = "Tabs.TabHost.xaml.cs";

    private static ShellSource Host() => ShellSource.Load(HostSource);

    private static AssignmentExpressionSyntax Assign(SyntaxNode node, string left) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == left);

    /// <summary>
    /// The gate is the first statement of everything that animates, and
    /// the cut is total: the motion-off arm returns without so much as a
    /// cleanup call, so a gated strip performs zero animation work. The
    /// settle is held to the same bar from the other side: it runs only
    /// when a lift is live in the field, so a gate that cut the grab
    /// also cut the handback. And decoration stands last in both drag
    /// handlers: the commit's state has landed before anything visual
    /// runs, never the reverse.
    /// </summary>
    [Fact]
    public void TheGate_IsTheFirstStatement_AndTheCutIsTotal()
    {
        var host = Host();
        foreach (var name in new[] { "StartLift", "FadeInAppearing" })
        {
            var first = Assert.IsType<IfStatementSyntax>(
                host.Method(name).Body!.Statements.First());
            Assert.Equal(
                "!TabStripMotion.Enabled(SystemAnimationsEnabled(), _highContrast)",
                first.Condition.ToString());
            Assert.Contains(
                first.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
                r => true);
            Assert.DoesNotContain(
                first.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
                c => true);
        }

        // No lift in the field, no settle work: the guard is the settle's
        // first statement, not a branch deep in its build-out.
        var settle = Assert.IsType<IfStatementSyntax>(
            host.Method("SettleLift").Body!.Statements.First());
        Assert.Equal("_lift is not { } lift", settle.Condition.ToString());

        // Decoration stands last in the engine's passes: the lift runs
        // after the seam cover is suppressed, and the settle runs after
        // the reconcile and the bridge update have spoken.
        var begin = host.Method("BeginHorizontalDragVisual");
        var seam = begin.Calls("SelectedTabSeamChanged?.Invoke").Single();
        var lift = begin.Calls("StartLift").Single();
        Assert.True(seam.Span.Start < lift.Span.Start,
            "the lift is decoration and stands last in the drag begin");

        var finish = host.Method("FinishHorizontalDrag");
        var bridge = finish.Calls("QueueBridgeUpdate").Single();
        var settleCall = finish.Calls("SettleLift").Single();
        Assert.True(bridge.Span.Start < settleCall.Span.Start,
            "the settle is decoration and stands last in the drag finish");
    }

    /// <summary>
    /// The lift is programmatic at rest. TabView exposes no machine
    /// velocity, so nothing in the gesture reads one: the grab springs
    /// the scale up on the lift tokens, the release springs it down to
    /// exactly rest on the drop-settle tokens, and no keyframe carries a
    /// scale the hand did not earn. The dragged element is named by the
    /// drag start's own args.Item -- the container TabView hands the
    /// event -- and nothing else.
    /// </summary>
    [Fact]
    public void TheLift_IsProgrammatic_AtRest()
    {
        var host = Host();

        // The dragged tab is the session's own item: the element the arm
        // resolved the press through by identity, not a container lookup
        // that can answer for a different slot.
        var begin = host.Method("BeginHorizontalDragVisual");
        var liftCall = begin.Calls("StartLift").Single();
        Assert.Equal("drag.Item", liftCall.Arg(0));

        // Springs only, and every parameter named: the WinRT spring
        // classes document no defaults, so an implicit period would be a
        // guess.
        var start = host.Method("StartLift");
        Assert.Contains(start.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "compositor.CreateSpringVector3Animation");
        Assert.Contains(start.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "compositor.CreateSpringScalarAnimation");
        Assert.DoesNotContain(
            start.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("KeyFrameAnimation", StringComparison.Ordinal));
        Assert.Equal("TabStripMotion.LiftDampingRatio",
            Assign(start, "scale.DampingRatio").Right.ToString());
        Assert.Contains("TabStripMotion.LiftPeriodMs",
            Assign(start, "scale.Period").Right.ToString());
        Assert.Contains("TabStripMotion.LiftScale",
            Assign(start, "scale.FinalValue").Right.ToString());
        Assert.Equal("TabStripMotion.LiftDampingRatio",
            Assign(start, "shadowIn.DampingRatio").Right.ToString());
        Assert.Equal("TabStripMotion.LiftShadowOpacity",
            Assign(start, "shadowIn.FinalValue").Right.ToString());

        // The handback lands at rest: the full identity, on the settle
        // tokens the vertical's drop uses.
        var settle = host.Method("SettleLift");
        Assert.Equal("TabStripMotion.SettleDampingRatio",
            Assign(settle, "settle.DampingRatio").Right.ToString());
        Assert.Contains("TabStripMotion.SettlePeriodMs",
            Assign(settle, "settle.Period").Right.ToString());
        Assert.Equal("new Vector3(1f, 1f, 1f)",
            Assign(settle, "settle.FinalValue").Right.ToString());

        // No machine velocity exists to read, and none may be invented:
        // the whole gesture is at rest, the flight's policy.
        foreach (var name in new[]
                 {
                     "BeginHorizontalDragVisual", "FinishHorizontalDrag",
                     "StartLift", "SettleLift"
                 })
        {
            Assert.DoesNotContain(
                host.Method(name).DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                m => m.Name.Identifier.ValueText == "Velocity");
        }
    }

    /// <summary>
    /// The shadow is born and dies with the lift. It is attached as the
    /// tab's child visual -- the one place that composes behind the
    /// tab's own content -- and FinishLift detaches it there, anchored
    /// to the element so the undo reaches the tab whatever the strip did
    /// to its slots in between. Its parameters are the shadow tokens,
    /// and the handback's shadow rides the unlift fade: the spring is
    /// the landing, and the fade is what keeps the shadow from popping
    /// off a tab that is still visibly settling.
    /// </summary>
    [Fact]
    public void TheShadow_IsBornAndDiesWithTheLift_AndTheHandbackIsAFade()
    {
        var host = Host();
        var start = host.Method("StartLift");
        var attach = start.Call("ElementCompositionPreview.SetElementChildVisual");
        Assert.Equal("item", attach.Arg(0));
        Assert.Equal("shadow", attach.Arg(1));
        Assert.Contains("TabStripMotion.LiftShadowBlurRadiusPx",
            Assign(start, "drop.BlurRadius").Right.ToString());
        Assert.Contains("TabStripMotion.LiftShadowOffsetYPx",
            Assign(start, "drop.Offset").Right.ToString());

        var finish = host.Method("FinishLift");
        Assert.NotEmpty(finish.Calls("lift.Guard.Stop"));
        var detach = finish.Call("ElementCompositionPreview.SetElementChildVisual");
        Assert.Equal("lift.Item", detach.Arg(0));
        Assert.Equal("null", detach.Arg(1));

        var fadeOut = Assign(host.Method("SettleLift"), "fadeOut.Duration");
        Assert.Contains("TabStripMotion.UnliftFadeMs", fadeOut.Right.ToString());
    }

    /// <summary>
    /// The collapse swap fades what appears and lingers over nothing.
    /// The chip fades in where it mints; a retiring chip arms the swap
    /// flag and the rebuild that re-enters its members fades exactly
    /// those -- rows the strip did not hold -- and never its own repair
    /// re-adds. The flag is cleared by the pass that consumed it, so a
    /// retirement with nothing to re-enter cannot fade some later pass.
    /// Removals are immediate: TabView has no item exit, and neither the
    /// chip's nor a tab's removal fades.
    /// </summary>
    [Fact]
    public void TheSwapFadesWhatAppears_AndItsClearIsPassOwned()
    {
        var host = Host();

        var mint = host.Method("AddGroupChip");
        Assert.Equal("chip", mint.Call("FadeInAppearing").Arg(0));

        var retire = host.Method("RemoveGroupChip");
        Assert.Single(retire.AssignsTo("_swapFadePending"));
        Assert.Empty(retire.Calls("FadeInAppearing"));
        Assert.Empty(host.Method("RemoveItem").Calls("FadeInAppearing"));

        var rebuild = host.Method("RebuildStripFromManager");
        var fade = rebuild.Calls("FadeInAppearing").Single();
        var guard = Assert.IsType<IfStatementSyntax>(
            fade.Ancestors().OfType<IfStatementSyntax>().First());
        Assert.Equal("_swapFadePending && !held.Contains(item)",
            guard.Condition.ToString());

        var clear = host.Method("ReconcileStripOrder").AssignsTo("_swapFadePending")
            .Single();
        Assert.Equal("false", clear.Right.ToString());

        // What appears arrives on the fade token -- the crossfade the
        // pin flight's handoff uses -- not on a duration of its own.
        var fadeIn = Assign(host.Method("FadeInAppearing"), "fadeIn.Duration");
        Assert.Contains("TabStripMotion.FadeMs", fadeIn.Right.ToString());
    }

    /// <summary>
    /// A mid-drag commit churns the dragged tab's slot, and the rebind
    /// refuses to let the lift depend on whether composition carries a
    /// visual's scale, center, and child sprite across that: every
    /// crossing re-asserts all three from the element's fresh visual,
    /// set rather than re-springed -- the vertical's RebindFollow
    /// precedent, and the reason the lift neither drops to rest nor
    /// re-bounces at the first commit.
    /// </summary>
    [Fact]
    public void A_crossing_rebinds_the_lift_set_not_respringed()
    {
        var host = Host();
        var commit = host.Method("CommitHorizontalCrossing");
        var move = commit.Call("_manager.Move");
        var rebind = commit.Call("RebindLift");
        Assert.True(
            move.SpanStart < rebind.SpanStart,
            "the rebind must follow the move: it re-asserts the lift on the "
            + "element the churn just re-slotted, so it cannot run first.");

        var rebindBody = host.Method("RebindLift");
        // The guard is first and names the one live lift on the dragged
        // element: nothing else's churn may rebind, and a no-lift strip
        // does no composition work.
        var first = Assert.IsType<IfStatementSyntax>(
            rebindBody.Body!.Statements.First());
        Assert.Equal(
            "_lift is not { } lift || !ReferenceEquals(lift.Item, item)",
            first.Condition.ToString());

        // Center from the element's own size, scale to the lift tokens as
        // a SET -- no spring in the rebind, or every crossing re-bounces
        // the tab the way a fresh grab does.
        Assert.Contains("item.ActualWidth", rebindBody
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "visual.CenterPoint").Right.ToString());
        Assert.Equal(
            "newVector3(TabStripMotion.LiftScale,TabStripMotion.LiftScale,1f)",
            Assign(rebindBody, "visual.Scale").Right.ToString()
                .Replace(" ", "").Replace("\n", "").Replace("\r", ""));
        Assert.Empty(rebindBody.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("CreateSpring", StringComparison.Ordinal)));

        // And the shadow rides the element again: the sprite is the one
        // part that anchors to the element tree, and the churn's answer
        // is to re-attach it, not to rebuild it.
        var attach = rebindBody.Call("ElementCompositionPreview.SetElementChildVisual");
        Assert.Equal("item", attach.Arg(0));
        Assert.Equal("lift.Shadow", attach.Arg(1));
    }

    /// <summary>
    /// The lift cannot outlive its supersede or its teardown. One lift
    /// lives in the field; a fresh grab finishes the one still handing
    /// back, the strip's Unloaded finishes it, and both callbacks that
    /// can end it -- the settle batch and the guard timer -- check the
    /// field before they run, because they fire long after the grab
    /// that armed them. FinishLift itself is the one door: it clears the
    /// field first, so a re-entrant ending cannot tear down a lift it
    /// does not own. The backstop is pinned armed, not merely
    /// configured: a guard that never starts stops nothing, and the
    /// wedged settle batch it exists for is exactly the case no batch
    /// callback reports.
    /// </summary>
    [Fact]
    public void TheLift_CannotOutliveItsSupersedeOrItsTeardown()
    {
        var host = Host();

        var start = host.Method("StartLift");
        Assert.Contains(start.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "FinishLift" && c.Arg(0) == "\"superseded\"");
        Assert.Single(start.Calls("lift.Guard.Start"));

        var unloaded = host.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "Unloaded");
        Assert.NotEmpty(unloaded.Right.Calls("FinishLift"));

        // settle-to-landing and the guard timer's timeout: two endings,
        // one guard shape, the pin flight's rule.
        var guarded = new[] { host.Method("StartLift"), host.Method("SettleLift") }
            .SelectMany(m => m.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
            .Where(f => f.Body is BlockSyntax block
                        && block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Any(c => c.CalleeText() == "FinishLift"))
            .ToList();
        Assert.Equal(2, guarded.Count);
        foreach (var callback in guarded)
        {
            var block = (BlockSyntax)callback.Body!;
            var first = Assert.IsType<IfStatementSyntax>(block.Statements.First());
            Assert.Equal("!ReferenceEquals(_lift, lift)", first.Condition.ToString());
        }

        var finish = host.Method("FinishLift");
        var field = Assert.IsType<IfStatementSyntax>(finish.Body!.Statements.First());
        Assert.Equal("_lift is not { } lift", field.Condition.ToString());
        Assert.Single(finish.AssignsTo("_lift"));
    }
}
