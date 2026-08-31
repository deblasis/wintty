using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The release-path pin flight, and the asymmetry it lives on. A
/// mid-drag boundary crossing commits through the tick loop: SetPinned,
/// Move, and a follow rebind -- the row never detaches, the churn's
/// own re-anchor is the motion, and there is nothing left to fly. A
/// release on the preview is the other landing shape: the churn
/// replaces the dragged element outright, so the flight carries a
/// ghost from where the eye held the row to the slot the preview
/// promised, and hands it to the real prefix-end row.
///
/// Three properties are pinned here. Both flight endpoints are read
/// before the commit, because the commit is what destroys the elements
/// the measurements ride. The flight is decoration -- gated on the
/// drag's motion flag, programmatic (velocity zero, no inertia), and
/// performing no manager calls -- so motion off keeps the release the
/// cut it always was and state never waits on an animation. And every
/// flight-ending callback is identity-guarded, because the batches and
/// the guard timer all fire long after the release that started them.
///
/// Wiring guards, not behaviour tests: what the flight looks like on
/// screen is only observable on a live strip. The motion constants'
/// values are pinned Core-side, next to the machine.
/// </summary>
public class TabPinFlightWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    private static AssignmentExpressionSyntax Assign(SyntaxNode node, string left) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == left);

    private static IEnumerable<AssignmentExpressionSyntax> Assignments(
        SyntaxNode node, string left) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == left);

    /// <summary>
    /// The start is the arranged row plus the follow offset, and the
    /// destination is the preview itself -- the element that has been
    /// sitting on the slot the drop is about to fill. Both reads live
    /// inside the drag's motion gate and both precede SetPinned: after
    /// it, the churn has replaced every element involved and a freshly
    /// inserted row has no arranged truth to measure. Reading late is
    /// how a flight departs from a ghost town.
    /// </summary>
    [Fact]
    public void BothFlightEndpoints_AreRead_BeforeTheCommit()
    {
        var released = Strip().Method("DragRelease");
        var gate = released.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_pinPreview is not null");
        var setPinned = gate.Calls("_manager.SetPinned").Single();

        var motionGate = gate.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "drag.MotionOn");
        Assert.Contains(motionGate.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "DraggedRowRect");
        Assert.Contains(motionGate.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "Canvas.GetLeft");
        Assert.Contains(motionGate.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "Canvas.GetTop");
        Assert.True(motionGate.Span.End < setPinned.Span.Start,
            "the flight endpoints must be measured before the commit churns the rows");

        // The destination is the preview's own geometry: the flight aims
        // at the promise, not at a re-derivation that could disagree
        // with what the user was shown.
        var dest = motionGate.Statement.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                == "_pinPreview is { } preview && preview.ActualWidth > 0");
        Assert.Contains("preview.Width", dest.Statement.ToString());
        Assert.Contains("VerticalTabPinnedRow.RowHeight", dest.Statement.ToString());
    }

    /// <summary>
    /// Motion off keeps the release byte-identical to the cut it was
    /// before the flight existed. The gate is the flight's first
    /// statement -- not a branch deep in the build-out -- so a motion-off
    /// release performs nothing past it; and the one caller sits after
    /// EndDrag, so even a motion-on release lands its state before the
    /// first frame of decoration. State first, decoration second, or
    /// the flight becomes a dependency.
    /// </summary>
    [Fact]
    public void MotionOff_KeepsTheReleaseTheCutItAlwaysWas()
    {
        var strip = Strip();
        var start = strip.Method("StartPinFlight");

        // The gate is the FIRST statement, and the cut is total: the
        // motion-off arm returns without so much as a cleanup call.
        var first = Assert.IsType<IfStatementSyntax>(start.Body!.Statements.First());
        // The polarity IS the semantics: motion off is the cut, so the
        // gate is the negation and the return sits in its arm.
        Assert.Equal("!drag.MotionOn", first.Condition.ToString());
        Assert.Contains(
            first.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(),
            r => true);
        Assert.DoesNotContain(
            first.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().StartsWith("_manager."));

        // The single caller runs after EndDrag: the row is already in
        // the manager and the drag is already torn down when the ghost
        // lifts off.
        var released = strip.Method("DragRelease");
        var gate = released.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_pinPreview is not null");
        var call = gate.Calls("StartPinFlight").Single();
        var end = gate.Calls("EndDrag").Single();
        Assert.True(end.Span.Start < call.Span.Start,
            "state lands through EndDrag before the flight decorates it");
        Assert.Empty(strip.Method("EvaluateDrag").Calls("StartPinFlight"));
        Assert.Empty(strip.Method("EvaluateRunDrag").Calls("StartPinFlight"));
    }

    /// <summary>
    /// The asymmetry, pinned on both landing shapes. The hysteresis
    /// commit IS motion: SetPinned, Move, RebindFollow -- the follow
    /// expression carries the row through the churn, so the row never
    /// detaches and a flight would paint a second copy of something
    /// that never went anywhere. The release shape is the only caller.
    /// Letting the tick loop fly too would stack a ghost on top of the
    /// very re-anchor that already explains the movement.
    /// </summary>
    [Fact]
    public void TheHysteresisCommit_IsItsOwnMotion_AndNeverFlights()
    {
        var strip = Strip();
        var evaluate = strip.Method("EvaluateDrag");

        // The churn path's own motion: the pin branch rebinds the follow
        // on the fresh element, which is what the user watches instead
        // of a flight.
        Assert.NotEmpty(evaluate.Calls("_manager.SetPinned"));
        Assert.NotEmpty(evaluate.Calls("_manager.Move"));
        var rebind = evaluate.Calls("RebindFollow").Single();
        var loop = rebind.Ancestors().OfType<WhileStatementSyntax>().First();
        Assert.Contains(loop.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "_manager.SetPinned");
        Assert.Empty(evaluate.Calls("StartPinFlight"));
        Assert.Empty(strip.Method("EvaluateRunDrag").Calls("StartPinFlight"));

        Assert.Single(strip.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "StartPinFlight"));
    }

    /// <summary>
    /// The flight is programmatic: the release hands the drag back at
    /// velocity zero, the travel itself is a keyframe on the tokens --
    /// never a spring -- and the one spring in the pipeline is the
    /// landing's deliberate bounce, on the settle tokens. Inheriting the
    /// gesture's velocity would fling the ghost past the slot its
    /// promise named, and the flight exists to keep that promise.
    /// </summary>
    [Fact]
    public void TheFlight_IsProgrammatic_AtVelocityZero()
    {
        var strip = Strip();
        var gate = strip.Method("DragRelease")
            .DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_pinPreview is not null");
        var end = gate.Calls("EndDrag").Single();
        Assert.Equal("settle: false", end.Arg(1));
        Assert.Equal("velocity: 0", end.Arg(2));

        var flight = strip.Method("StartPinFlight");
        Assert.Contains(flight.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("CreateVector3KeyFrameAnimation"));
        Assert.DoesNotContain(
            flight.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("CreateSpringVector3Animation"));
        var duration = Assign(flight, "fly.Duration");
        Assert.Contains("TabStripMotion.PinFlightMs", duration.Right.ToString());

        // The landing's bounce is the only spring, and it names the
        // settle tokens explicitly -- the WinRT spring classes document
        // no defaults, so an implicit period would be a guess.
        var settle = strip.Method("StartPinSettle");
        Assert.Equal("TabStripMotion.PinSettleDampingRatio",
            Assign(settle, "spring.DampingRatio").Right.ToString());
        Assert.Contains("TabStripMotion.PinSettlePeriodMs",
            Assign(settle, "spring.Period").Right.ToString());

        // The handoff crossfades both sides on the fade token, one
        // batch, one clock -- no frame where both are gone.
        var handback = strip.Method("StartPinHandback");
        foreach (var fade in new[] { "fadeOut.Duration", "fadeIn.Duration" })
            Assert.Contains("TabStripMotion.FadeMs",
                Assign(handback, fade).Right.ToString());
    }

    /// <summary>
    /// Every callback that can end a flight checks that the flight it
    /// was created for is still the one in the field. The batches and
    /// the guard timer all complete long after the release: a
    /// superseding flight, a new drag, or a teardown has replaced the
    /// field by then, and a stale callback running its phase or its
    /// landing would tear down a flight it does not own -- or fade out
    /// a row the new flight just hid.
    /// </summary>
    [Fact]
    public void EveryFlightEndingCallback_IsIdentityGuarded()
    {
        var methods = new[]
        {
            Strip().Method("StartPinFlight"),
            Strip().Method("StartPinSettle"),
            Strip().Method("StartPinHandback"),
        };
        var guarded = methods.SelectMany(m => m.DescendantNodes()
                .OfType<AnonymousFunctionExpressionSyntax>())
            .Where(f => f.Body is BlockSyntax block && block.Statements.Count > 0
                        && block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Any(c => c.CalleeText() is "StartPinSettle"
                                or "StartPinHandback"
                                or "FinishPinFlight"))
            .ToList();

        // fly-to-settle, settle-to-handback, handback-to-landing, and the
        // guard timer's timeout: four handoffs, one guard shape.
        Assert.Equal(4, guarded.Count);
        foreach (var callback in guarded)
        {
            var block = (BlockSyntax)callback.Body!;
            var first = Assert.IsType<IfStatementSyntax>(block.Statements.First());
            Assert.Equal("!ReferenceEquals(_pinFlight, flight)",
                first.Condition.ToString());
        }
    }

    /// <summary>
    /// The flight cannot outlive the strip or dodge the census. The
    /// leak count folds the live flight in -- one field, zero or one --
    /// so the trace oracle's any-N-above-zero rule covers it like every
    /// other composition the strip believes it is driving. The
    /// backstop is pinned armed, not merely configured: a guard that
    /// never starts stops nothing, and the wedged batch it exists for
    /// is exactly the case no batch callback reports. Teardown
    /// finishes it, a fresh drag supersedes it, and landing restores the
    /// row's opacity by writing it: stopping an animation reverts the
    /// property to its set value, so an assumed end state would land
    /// the row at whatever it was before the flight hid it.
    /// </summary>
    [Fact]
    public void TheFlight_NeverOutlivesTheStrip_NorTheCensus()
    {
        var strip = Strip();

        Assert.Contains("_pinFlight",
            strip.Method("CountLeakedMotion").ExpressionBody!.ToString());

        var unloaded = strip.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "Unloaded");
        Assert.NotEmpty(unloaded.Right.Calls("FinishPinFlight"));
        Assert.Single(strip.Method("StartDragVisual").Calls("FinishPinFlight"));

        Assert.Single(strip.Method("StartPinFlight").Calls("flight.Guard.Start"));

        var finish = strip.Method("FinishPinFlight");
        Assert.NotEmpty(finish.Calls("flight.Guard.Stop"));
        var restored = Assign(finish, "flight.Row.Opacity");
        Assert.Equal("1", restored.Right.ToString());
        Assert.Contains(finish.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("PreviewHost.Children.Remove"));
    }

    /// <summary>
    /// The shelf rows' two pointer-adjacent states share one painter,
    /// and focus wins: the condition names focus first and the pointer
    /// only as the second disjunct, so a keyboard user's indicator does
    /// not flicker off because the pointer wandered across, and a row
    /// that is neither stays transparent -- the lane it sits on. The
    /// exit path repaints through the same painter rather than writing
    /// a fill of its own, because a private restore is how the two
    /// states drift apart.
    /// </summary>
    [Fact]
    public void HoverAndFocus_ShareOnePainter_AndFocusWins()
    {
        var strip = Strip();
        var paint = strip.Method("PaintShelfRow");
        var fill = Assign(paint, "row.Background");
        var ternary = Assert.IsType<ConditionalExpressionSyntax>(fill.Right);
        // Whitespace-normalized: the condition spans two source lines,
        // and the pin is the ORDER -- focus named first, pointer second.
        Assert.Equal(
            "row.FocusState != FocusState.Unfocused "
            + "|| ReferenceEquals(_hoveredShelfRow, row)",
            System.Text.RegularExpressions.Regex.Replace(
                ternary.Condition.ToString(), @"\s+", " "));
        Assert.Equal("TransparentBrush", ternary.WhenFalse.ToString());
        Assert.DoesNotContain(
            paint.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "Color.FromArgb");

        // Enter records the pointer before painting -- the read and the
        // paint must agree on the same row -- and exit repaints through
        // the painter, writing no background of its own.
        var exited = strip.Method("OnShelfRowPointerExited");
        Assert.Single(exited.Calls("PaintShelfRow"));
        Assert.Empty(Assignments(exited, "row.Background"));

        var entered = strip.Method("OnShelfRowPointerEntered");
        var record = entered.AssignsTo("_hoveredShelfRow").Single();
        var repaint = entered.Calls("PaintShelfRow").Single();
        Assert.True(record.Span.Start < repaint.Span.Start,
            "hover is recorded before it is painted");

        // Both focus transitions route through the painter too: one
        // writer for the row's fill, or the states erase each other.
        var focus = strip.Method("OnPinnedRowFocusVisual");
        Assert.Single(focus.Calls("PaintShelfRow"));
        Assert.Empty(Assignments(focus, "row.Background"));
    }
}
