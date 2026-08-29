using System.Linq;
using Ghostty.Core.Config;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The pane startup glow is a startup race in disguise, so its wiring is
/// what can break silently: a spawn signal raised one statement too early,
/// a mount positioned off the render-thread transform, a glow whose timer
/// outlives the pane it was measuring.
///
/// The lifecycle itself is a pure state machine covered by
/// <see cref="Ghostty.Tests.Panes.PaneStartupGlowStateTests"/>. What is
/// only observable here is that the four pieces stay bolted together the
/// way the fix left them.
/// </summary>
public class PaneStartupGlowWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Panes.PaneHost.cs");

    private static ShellSource Terminal() => ShellSource.Load("Controls.TerminalControl.xaml.cs");

    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static string ZIndex(ShellSource source, string element) =>
        source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "Canvas.SetZIndex" && i.Arg(0) == element)
            .Arg(1);

    /// <summary>
    /// SurfaceSpawned is the glow's start gun, and it is only correct at the
    /// tail of OnLoaded: before that statement the surface either does not
    /// exist or is not registered, and the handler would find no leaf to
    /// mount the glow over. Raised a line earlier, the first pane opens
    /// dark and nothing fails.
    /// </summary>
    [Fact]
    public void SurfaceSpawned_IsRaised_AsTheLastStatementOfOnLoaded()
    {
        var body = Terminal().Method("OnLoaded").Body!.Statements;
        Assert.True(body.Count > 1, "OnLoaded should have more than one statement");

        var raise = body.OfType<ExpressionStatementSyntax>()
            .Single(s => s.ToString().Contains("SurfaceSpawned"));
        Assert.Equal(body.Last(), raise);
    }

    /// <summary>
    /// A control whose surface is gone must not keep a subscriber that
    /// starts glows. DisposeSurface nulls every other event for exactly
    /// this reason; the new one has to be in that list.
    /// </summary>
    [Fact]
    public void DisposeSurface_DropsTheSurfaceSpawnedSubscriber()
    {
        var body = Terminal().Method("DisposeSurface").Body!.Statements;

        var assignments = body.SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "SurfaceSpawned")
            .ToList();

        var dropped = Assert.Single(assignments);
        Assert.Equal("null", dropped.Right.ToString());
    }

    /// <summary>
    /// Start on the surface spawn, end on the first render, both wired where
    /// every leaf's control is born. Wiring one of them somewhere else (a
    /// focus handler, a Loaded handler) would silently skip splits and
    /// restored panes, which are the panes a glow is most useful on.
    /// </summary>
    [Fact]
    public void CreateTerminal_WiresSpawnToStart_AndFirstRenderToEnd()
    {
        var body = Host().Method("CreateTerminal").Body!.Statements;

        var wired = body.SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Right.ToString() is "OnLeafSurfaceSpawned" or "OnLeafFirstRender")
            .Select(a => a.Left.ToString())
            .ToList();

        Assert.Equal(["t.SurfaceSpawned", "t.FirstRender"], wired);
    }

    /// <summary>
    /// Closing a leaf tears down its glow. Without this the state machine's
    /// timer keeps firing against a disposed control and the mount stays on
    /// the overlay after the pane is gone.
    /// </summary>
    [Fact]
    public void TeardownLeaf_TearsTheGlowDown()
    {
        Assert.Single(Host().Method("TeardownLeaf").Body!.Statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TeardownGlow");
    }

    /// <summary>
    /// A soft-closed leaf keeps its shell alive for undo but leaves the visual
    /// tree at once, so its glow would keep orbiting a frozen rectangle until
    /// the cap unless the branch closes the state itself. Stated against the
    /// soft-close branch rather than the method as a whole: the hard-close
    /// branch already reaches the glow through TeardownLeaf, so a method-wide
    /// scan would keep passing with only that path left.
    /// </summary>
    [Fact]
    public void CloseLeaf_SoftCloseBranch_ClosesTheClosingLeafGlow()
    {
        // Two overloads share the name, and ShellSource.Method refuses that,
        // so name the one with the undoable flag.
        var close = Host().Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CloseLeaf"
                         && m.ParameterList.Parameters.Count == 2);

        var softClose = close.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "softClose");
        var retained = Assert.IsType<BlockSyntax>(softClose.Statement);

        // The undo snapshot stays in the branch; the glow close is added to
        // it, not swapped for it.
        Assert.Contains(retained.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "CaptureForUndo");

        var closed = Assert.Single(retained.DescendantNodes()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().EndsWith(".Close", System.StringComparison.Ordinal));

        // The closed state is the CLOSING leaf's, resolved by that leaf's own
        // terminal, so a sibling's glow cannot be closed in its place.
        var guard = close.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                == "_glowStates.TryGetValue(leaf.Terminal(), out var closingGlow)");
        Assert.Contains(closed, guard.DescendantNodes());
    }

    /// <summary>
    /// Window teardown sweeps whatever is still in flight. Stated against
    /// the glow dictionary rather than against the tree walk, because the
    /// tree walk is gated on every leaf already being closed and a glow is
    /// alive exactly in the window the gate excludes.
    /// </summary>
    [Fact]
    public void DisposeAllLeaves_SweepsEveryLiveGlow()
    {
        var loops = Host().Method("DisposeAllLeaves").Body!.Statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<ForEachStatementSyntax>()
            .Where(f => f.Expression.ToString() == "_glowStates.Keys.ToList()")
            .ToList();

        var sweep = Assert.Single(loops);
        Assert.Contains(sweep.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(), i => i.CalleeText() == "TeardownGlow");
    }

    /// <summary>
    /// The glow rides just under the focus stroke, and the mount is sized in
    /// the layout handler rather than from a one-shot spawn-time measurement
    /// -- the leaf has almost never been arranged when its surface spawns.
    /// </summary>
    [Fact]
    public void GlowMount_SitsUnderTheActiveBorder_AndIsTrackedByLayout()
    {
        var host = Host();

        Assert.Equal("998", ZIndex(host, "mount"));
        Assert.Equal("999", ZIndex(host, "_activeBorderFrame"));

        // The mount is a child of the overlay, the same surface the active
        // border draws on, so the two z-values are the whole ordering.
        Assert.Contains(host.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "_highlightOverlay.Children.Add" && i.Arg(0) == "mount");
    }

    /// <summary>
    /// The mount is positioned from the layout-slot chain, never from
    /// TransformToVisual: at cold start the idle compositor does not commit
    /// the render-thread transform for ~750ms, so a transform-based mount
    /// would strand at zero size and the first pane would open with no glow
    /// at all. LeafLayoutBounds exists for the other overlay layers for the
    /// same reason; the glow has to use it rather than grow a second answer.
    /// </summary>
    [Fact]
    public void GlowMount_IsPositionedFromLayoutSlots()
    {
        var host = Host();

        var position = host.Method("PositionGlowMount");
        var calls = position.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>();
        Assert.Single(calls, i => i.CalleeText() == "LeafLayoutBounds");
        Assert.DoesNotContain(calls,
            i => i.CalleeText().EndsWith("TransformToVisual", System.StringComparison.Ordinal));

        var leafBounds = host.Method("LeafLayoutBounds");
        Assert.Contains(leafBounds.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().EndsWith("LayoutInformation.GetLayoutSlot", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The kill switch has to be honored where the glow starts, not somewhere
    /// downstream that a later refactor could drop: an enabled config that
    /// still spawns a glow is a setting that lies.
    /// </summary>
    [Fact]
    public void OnLeafSurfaceSpawned_ChecksEnablementBeforeAnythingElse()
    {
        var guards = Host().Method("OnLeafSurfaceSpawned").Body!.Statements
            .OfType<IfStatementSyntax>()
            .Select(i => i.Condition.ToString())
            .ToList();

        Assert.Contains("!_startupGlowEnabled", guards);
        Assert.True(
            guards.IndexOf("!_startupGlowEnabled") < guards.IndexOf("_glowStates.ContainsKey(terminal)"),
            "the enablement guard must come before the already-glowing guard");
    }

    /// <summary>
    /// The key is Windows-only, so libghostty calls it an unknown field and
    /// only the app's own read makes it do anything. Registered or not, the
    /// diagnostic noise is the difference between a setting and a bug report.
    /// </summary>
    [Fact]
    public void PaneStartupGlow_IsARegisteredWindowsOnlyKey_DefaultingOn()
    {
        Assert.True(WindowsOnlyKeys.Contains("pane-startup-glow"));
        Assert.Single(WindowsOnlyKeys.All, e => e.Key == "pane-startup-glow");

        var read = ConfigService().Method("ReadFlagsCore").Calls("WindowsOnlyKeyParsers.ParseBool")
            .Single(c => c.Arg(0).Contains("\"pane-startup-glow\""));
        Assert.Equal("defaultValue: true", read.Arg(1));
    }

    /// <summary>
    /// The window pushes the glow config alongside the border colours, per
    /// tab, in the same pass. Pinned against the enclosing loop rather than
    /// the method as a whole, because that is the part that carries the
    /// guarantee: hoisted above the loop the push configures no tab that
    /// exists only as a loop iteration, and hoisted below it the push lands
    /// after a newly created host's deferred Loaded, so that first pane reads
    /// the default (enabled, default colours) instead of the user's.
    /// </summary>
    [Fact]
    public void ApplyPerTabChrome_PushesTheGlowConfigInsideThePerTabLoop()
    {
        var loop = Assert.Single(MainWindow().Method("ApplyPerTabChrome").Body!.Statements
                .SelectMany(s => s.DescendantNodesAndSelf())
                .OfType<ForEachStatementSyntax>(),
            f => f.Expression.ToString() == "_tabManager.Tabs");

        var push = Assert.Single(loop.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().EndsWith("SetStartupGlowConfig", System.StringComparison.Ordinal));
        Assert.Equal("_configService.PaneStartupGlow", push.Arg(0));
    }

    /// <summary>
    /// The glow pass rides the leaf set UpdateHighlightPosition already walked
    /// for the dim rects, and PositionGlowMount takes that leaf instead of
    /// finding its own: the layout pass runs on every layout tick, so a
    /// per-mount Leaves().FirstOrDefault() is a full tree traversal plus an
    /// enumerator allocation per mount per tick, spent re-deriving an answer
    /// the caller was holding. Both halves are pinned, because reverting
    /// either one alone puts the walk back.
    /// </summary>
    [Fact]
    public void UpdateHighlightPosition_GlowPass_ReusesTheLeavesItAlreadyWalked()
    {
        var host = Host();

        var position = host.Method("PositionGlowMount");
        Assert.Contains(position.ParameterList.Parameters,
            p => p.Type?.ToString() == "LeafPane?");
        Assert.DoesNotContain(position.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "PaneTree.Leaves");

        var glowPass = host.Method("UpdateHighlightPosition").DescendantNodesAndSelf()
            .OfType<CommonForEachStatementSyntax>()
            .Single(f => f.Expression.ToString() == "_glowMounts");

        var calls = glowPass.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>();
        Assert.DoesNotContain(calls, i => i.CalleeText() == "PaneTree.Leaves");
        Assert.DoesNotContain(calls,
            i => i.CalleeText().EndsWith("FirstOrDefault", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Dispose releases every composition object the glow creates, not only
    /// the ones it was first written with: a forever key-frame animation left
    /// running keeps the compositor animating a brush nobody paints with, and
    /// a gradient stop is a composition object in its own right. Stated as the
    /// parsed file, because a unit test cannot build a compositor; the list is
    /// explicit so dropping one of them from Dispose has to delete a line
    /// here too.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesEveryCompositionObjectTheGlowCreated()
    {
        var dispose = ShellSource.Load("Panes.PaneStartupGlow.cs").Method("Dispose");
        var calls = dispose.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Select(i => i.CalleeText())
            .ToList();

        foreach (var owner in new[]
                 {
                     "_shapeVisual", "_coreShape", "_haloShape", "_geometry",
                     "_coreBrush", "_coreStops", "_haloBrush", "_haloStops",
                     "_fade", "_orbit", "_easing",
                 })
        {
            Assert.Contains(calls, c => c == owner + ".Dispose");
        }

        // The stops are enumerated out of their collection, so each collection
        // has to still be open when Dispose reaches it.
        var invokes = dispose.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>();
        foreach (var stops in new[] { "_coreStops", "_haloStops" })
            Assert.Contains(invokes, i => i.CalleeText() == "DisposeStops" && i.Arg(0) == stops);

        // Stopping an animation on a closed object throws, so the stops all
        // come before the first release.
        var stopped = calls.FindIndex(c => c.EndsWith(".StopAnimation", System.StringComparison.Ordinal));
        var released = calls.FindIndex(c => c.EndsWith(".Dispose", System.StringComparison.Ordinal));
        Assert.True(stopped >= 0 && stopped < released,
            $"the first StopAnimation (index {stopped}) must come before the first Dispose (index {released})");
    }
}
