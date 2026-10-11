using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The all-panes liveness op: every live pane in the active tab must answer
/// for itself, because the defect this exists for is not a pane failing its
/// own scenario -- it is a pane sitting empty and unresponsive while every
/// named leg of a run passes around it. Per pane the op reports: a live
/// surface, Visible through the WHOLE ancestor chain, an effective opacity of
/// 1 composed through every ancestor (the composite, not the pane's own
/// property: an ancestor left at partial opacity fails the pane), a non-zero
/// size, a hit-test that resolves the pane's own subtree over its own slot,
/// and typed input echoing back through the real in-process path. Beside the
/// rows, the dispatcher's own round trip under a stated budget.
///
/// What is only observable here is the wiring of each arm: the ancestor
/// walk, the real hit-test, the send-and-readback echo, and the seeded
/// defect that makes the oracle's red demonstrable on demand. Whether a pane
/// really is dead is answered by the op against a live app; these pins keep
/// each arm from quietly ceasing to ask.
/// </summary>
public class TestSeamUiLivenessWiringTests
{
    private static SwitchSectionSyntax TheOp() =>
        ShellSource.Load("Testing.TestSeam.cs")
            .Case("ExecuteOnUiThreadAsync", "\"ui-liveness\"");

    private static MethodDeclarationSyntax TheWalk() =>
        ShellSource.Load("Testing.TestSeam.cs").Method("UiLivenessJsonAsync");

    /// <summary>
    /// The statements of a case body with its opening block unwrapped, in
    /// source order -- the shape the seam's gate pins read.
    /// </summary>
    private static List<StatementSyntax> Statements(SwitchSectionSyntax section) =>
        section.Statements
            .OfType<BlockSyntax>().SelectMany(b => b.Statements)
            .Concat(section.Statements.Where(s => s is not BlockSyntax))
            .ToList();

    /// <summary>
    /// The op refuses two vacuous shapes before it answers anything: no
    /// active tab, and no live panes. A liveness answer over zero panes
    /// reads as "every pane answered every arm" to any judge that only
    /// checks failure fields, so the refusal is what keeps an activation
    /// flip or a dead tree from ever reading as a green walk.
    /// </summary>
    [Fact]
    public void TheOpExists_AndRefusesAWalkWithNothingInIt()
    {
        var op = TheOp();
        SeamBuildGate.AssertInsideTheBuildGate(
            ShellSource.Load("Testing.TestSeam.cs").Root, op.Span, "the ui-liveness op");

        var text = op.ToString();
        Assert.Contains("no active tab", text, StringComparison.Ordinal);
        Assert.Contains("no live panes", text, StringComparison.Ordinal);
        Assert.Single(op.Calls("UiLivenessJsonAsync"));
    }

    /// <summary>
    /// The echo arm types markers into live shells through the same
    /// in-process path send-text uses, so the op carries send-text's own
    /// second opt-in, and carries it FIRST: a harness that only moves chrome
    /// must not be able to make a shell type, and a gate that ran after the
    /// walk had already sent would not be a gate.
    /// </summary>
    [Fact]
    public void TheEchoTypesIntoLiveShells_SoTheOpSitsBehindTheInputGate()
    {
        var first = Assert.IsType<IfStatementSyntax>(Statements(TheOp()).First());
        Assert.Equal("!_inputAllowed", first.Condition.ToString());
        Assert.Contains("InputEnvVar", first.Statement.ToString(), StringComparison.Ordinal);

        // And the walk really does send: the echo arm rides the shell's own
        // input path, never a synthesised keystroke.
        Assert.NotEmpty(TheWalk().CallsEndingWith("TestSeamSendText"));
    }

    /// <summary>
    /// Every arm is its own field on every row, so a judge can name the pane
    /// AND the arm that failed from the answer alone. A walk that folds two
    /// arms into one boolean reds vaguely, which is the failure mode this
    /// pins out of existence.
    /// </summary>
    [Fact]
    public void EveryPaneRowAnswersEveryArm_AsItsOwnField()
    {
        var walk = TheWalk().ToString();
        foreach (var field in new[]
        {
            // per-pane arms
            "\"leaf\"", "\"surfaceLive\"", "\"visibleAll\"",
            "\"compositeOpacity\"", "\"width\"", "\"height\"",
            "\"hitSelf\"", "\"echoed\"", "\"echoRetried\"", "\"echoMs\"",
            // the global timing, with the row count that proves the walk
            // was not empty
            "\"dispatcherTurnMs\"", "\"framesTicked\"", "\"paneCount\"",
        })
        {
            Assert.Contains(field, walk, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The opacity arm composes through EVERY visual ancestor: the pane's
    /// own Opacity of 1 says nothing about what the eye gets when an
    /// ancestor above it is at partial opacity -- the exact shape a leaked
    /// fade or a half-applied cover leaves behind. The walk must multiply
    /// each ancestor's Opacity into the composite and fail visibility on a
    /// collapsed ancestor; reading the pane's own property alone is the
    /// regression.
    /// </summary>
    [Fact]
    public void TheOpacityArmComposesThroughEveryAncestor()
    {
        var walk = TheWalk().ToString();

        // The parents are reached through the visual tree, not the property
        // the pane reports about itself.
        Assert.Contains("VisualTreeHelper.GetParent", walk, StringComparison.Ordinal);
        Assert.Contains("composite *= parentFe.Opacity", walk, StringComparison.Ordinal);
        Assert.Contains(
            "parentFe.Visibility != Microsoft.UI.Xaml.Visibility.Visible",
            walk, StringComparison.Ordinal);

        // The composite starts at the pane's own opacity, so the product
        // covers the whole chain, pane included.
        Assert.Contains("= terminal.Opacity", walk, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hit arm asks the real hit-test over the pane's own slot: the
    /// centre of the pane's rect, through FindElementsInHostCoordinates,
    /// resolved to THIS pane's terminal subtree by walking the hit's
    /// ancestors. The defect class is a pane whose hit region is dead while
    /// the pane still renders -- a pointer over that slot lands in whatever
    /// is underneath, so only the real hit-test catches it. The retry is
    /// bounded (a tree that just rebuilt can sit a few frames from its
    /// arrange) and pumps between attempts.
    /// </summary>
    [Fact]
    public void TheHitArmAsksTheRealHitTest_OverThePanesOwnSlot()
    {
        var walk = TheWalk().ToString();
        Assert.Contains(
            "FindElementsInHostCoordinates", walk, StringComparison.Ordinal);
        // The centre, not a corner: a corner can sit on a neighbour's
        // splitter while the pane's own slot is dead.
        Assert.Contains("rect.Width / 2", walk, StringComparison.Ordinal);
        Assert.Contains("rect.Height / 2", walk, StringComparison.Ordinal);
        // Bounded attempts, with a dispatcher turn between them.
        Assert.Contains("hitAttempt < 5", walk, StringComparison.Ordinal);
        Assert.Contains("WaitForLowPriorityAsync", walk, StringComparison.Ordinal);
        // And the resolution walks the hit's ancestors to the terminal.
        Assert.Contains("ReferenceEquals(node, terminal)", walk, StringComparison.Ordinal);
    }

    /// <summary>
    /// The echo arm is a round trip: the marker goes in through the shell's
    /// real input path and comes back out of the cell grid libghostty
    /// reports, so "the input path is dead" and "the pane renders but never
    /// got the bytes" are the same finding and both are caught. One re-send
    /// before the path is called dead separates a swallowed first send from
    /// a dead one, and the pane is remembered so later walks re-probe it
    /// cheaply instead of paying the full budget every step.
    /// </summary>
    [Fact]
    public void TheEchoRidesTheRealInputPath_AndReadsTheGridBack()
    {
        var seam = ShellSource.Load("Testing.TestSeam.cs");
        var walk = TheWalk().ToString();

        // The marker is unique per pane per walk, so a stale echo from an
        // earlier walk can never answer for this one.
        Assert.Contains("ZL", walk, StringComparison.Ordinal);
        Assert.Contains("_uiLivenessStamp", walk, StringComparison.Ordinal);

        // The readback is the grid, read per row.
        var readback = seam.Method("UiLivenessSurfaceTextContains");
        Assert.Single(readback.Calls("Interop.NativeMethods.SurfaceReadCells"));

        // The retry and the memory: a first send can be swallowed by a
        // focus transition, and a pane that stays dead is re-probed at the
        // short budget rather than re-costing the walk its full one.
        Assert.Contains("echoRetried = true", walk, StringComparison.Ordinal);
        Assert.Contains("_uiLivenessDeadEcho.Add", walk, StringComparison.Ordinal);
        Assert.Contains("_uiLivenessDeadEcho.Remove", walk, StringComparison.Ordinal);
        Assert.Contains("AddMilliseconds(1500)", walk, StringComparison.Ordinal);
        Assert.Contains("AddMilliseconds(600)", walk, StringComparison.Ordinal);
    }

    /// <summary>
    /// The frame counter is evidence, not an arm: an idle pane renders
    /// through its own swap chain, so the composition clock legitimately
    /// stands still. It is subscribed for the walk and unsubscribed in a
    /// finally, because CompositionTarget.Rendering is static and a leak
    /// here would pin a closed window's handler forever.
    /// </summary>
    [Fact]
    public void TheFrameCounterIsSubscribedForTheWalk_AndRetiredInAFinally()
    {
        var walk = TheWalk();
        var text = walk.ToString();
        Assert.Contains("CompositionTarget.Rendering +=", text, StringComparison.Ordinal);

        var fin = walk.DescendantNodes().OfType<TryStatementSyntax>()
            .SelectMany(t => t.Finally?.Block?.Statements ?? Enumerable.Empty<StatementSyntax>())
            .ToList();
        Assert.Contains(
            fin.Select(s => s.ToString()),
            s => s.Contains("CompositionTarget.Rendering -=",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The seeded defect is the oracle's on-demand red: one ancestor of one
    /// pane, at a partial opacity, for exactly one walk. It must apply to an
    /// ANCESTOR (the composite arm exists to catch exactly that), the seed
    /// must be restored no matter how the walk ends, and the answer must say
    /// the walk was seeded so a judge never mistakes the demonstration for
    /// the defect.
    /// </summary>
    [Fact]
    public void TheSeededDefectDimsAnAncestor_AndAlwaysRestoresIt()
    {
        var op = TheOp();
        var text = op.ToString();

        // Strict parse: a misspelled seed that silently walks clean is
        // indistinguishable from an unseeded one.
        Assert.Contains("seedAncestorOpacity", text, StringComparison.Ordinal);
        Assert.Contains("JsonValueKind.Number", text, StringComparison.Ordinal);

        // The target is a PARENT reached through the visual tree, never the
        // pane itself.
        Assert.Contains("defectTarget = parentFe", text, StringComparison.Ordinal);

        // The restore runs in a finally, so a walk that threw still puts the
        // ancestor back.
        var fin = op.DescendantNodes().OfType<TryStatementSyntax>()
            .SelectMany(t => t.Finally?.Block?.Statements ?? Enumerable.Empty<StatementSyntax>())
            .ToList();
        Assert.Contains(
            fin.Select(s => s.ToString()),
            s => s.Contains("defectTarget.Opacity = defectBefore",
                StringComparison.Ordinal));

        // And the answer names the seed.
        var walk = TheWalk().ToString();
        Assert.Contains("\"seededAncestorOpacity\"", walk, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dispatcher arm measures the seam's own pump: one Low-priority
    /// round trip, timed, reported. The budget a judge holds it to lives in
    /// the harness, but the measurement has to be of the dispatcher the ops
    /// themselves ride, or a hung queue would answer this op quickly while
    /// stalling everything else.
    /// </summary>
    [Fact]
    public void TheDispatcherArmTimesTheSeamsOwnPump()
    {
        var walk = TheWalk().ToString();
        Assert.Contains(
            "WaitForLowPriorityAsync(window.DispatcherQueue)",
            walk, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.StartNew()", walk, StringComparison.Ordinal);
    }
}
