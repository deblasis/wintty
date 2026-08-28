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
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "TeardownGlow"));
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

        var zindex = host.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "Canvas.SetZIndex")
            .ToDictionary(i => i.Arg(0), i => i.Arg(1));

        Assert.Equal("998", zindex["mount"]);
        Assert.Equal("999", zindex["_activeBorderFrame"]);

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
        Assert.Single(position.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "LeafLayoutBounds"));
        Assert.Empty(position.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("TransformToVisual", System.StringComparison.Ordinal)));

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

        Assert.Contains("_startupGlowEnabled", guards);
        Assert.True(
            guards.IndexOf("_startupGlowEnabled") < guards.IndexOf("_glowStates.ContainsKey(terminal)"),
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
    /// tab, in the same pass. A push from a later hook would land after a
    /// newly created host's deferred Loaded, and that first pane would read
    /// the default (enabled, default colours) instead of the user's.
    /// </summary>
    [Fact]
    public void ApplyPerTabChrome_PushesTheGlowConfigWithTheBorderColors()
    {
        var pushes = MainWindow().Method("ApplyPerTabChrome").Body!.Statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("SetStartupGlowConfig", System.StringComparison.Ordinal))
            .ToList();

        var push = Assert.Single(pushes);
        Assert.Equal("_configService.PaneStartupGlow", push.Arg(0));
    }
}
