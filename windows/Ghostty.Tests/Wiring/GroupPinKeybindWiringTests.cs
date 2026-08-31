using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The tail-rung keybind plumbing for the tab-shell verbs, as wiring
/// guards: pin_tab/unpin_tab/move_group arrive from libghostty as plain
/// tags, so every hop between the tag and the TabManager op is a place a
/// silent misroute can live. Routing facts here; mapping facts in
/// ApprtActionMapTests; the renderable catalog facts in
/// KeybindActionCatalogTests.
/// </summary>
public class GroupPinKeybindWiringTests
{
    private static ShellSource Router() => ShellSource.Load("Input.PaneActionRouter.cs");
    private static ShellSource Host() => ShellSource.Load("Hosting.GhosttyHost.cs");

    /// <summary>
    /// A keybind naming move_group lands on the manager's MoveGroup, the
    /// same commit the drag surfaces hand over. The polarity decides the
    /// neighbour run whose width the run is displaced by: left swaps with
    /// the run ending at start-1, right with the run beginning at
    /// start+run.Count. Inverting either turns "move group left" into a
    /// jump across two groups.
    /// </summary>
    [Fact]
    public void GroupMove_RoutesThroughMoveGroup_ByNeighbourRunWidth()
    {
        var section = Router().Case("Invoke", "PaneAction.MoveGroupRight");
        var moves = section.Calls("_tabs.MoveGroup");
        Assert.Equal(2, moves.Count);

        var ordered = moves.OrderBy(m => m.Span.Start).ToList();
        Assert.Equal(
            "start - _tabs.RunOf(_tabs.Tabs[start - 1]).Count",
            ordered[0].Arg(1));
        Assert.Equal(
            "start + _tabs.RunOf(_tabs.Tabs[start + run.Count]).Count",
            ordered[1].Arg(1));
    }

    /// <summary>
    /// The refusals are positional, not a policy of their own: an
    /// ungrouped (or pinned, which implies ungrouped) active tab falls out
    /// before any index math, the left arm refuses at the pinned prefix,
    /// and the right arm refuses at the end of the list -- MoveTabLeft/Right's
    /// guard shape, one level up.
    /// </summary>
    [Fact]
    public void GroupMove_GuardsPrecedeTheirMove_AtBothBoundaries()
    {
        var section = Router().Case("Invoke", "PaneAction.MoveGroupRight");

        var conditions = section.DescendantNodes().OfType<IfStatementSyntax>()
            .Select(i => i.Condition.ToString())
            .ToList();
        Assert.Contains("group is null", conditions);
        Assert.Contains("start <= _tabs.PinCount", conditions);
        Assert.Contains("start + run.Count >= _tabs.Tabs.Count", conditions);

        // Span order inside the section: the ungrouped guard, then the
        // left guard before the left move, then the right guard before
        // the right move. A guard that drifts after its move would let a
        // -1 index (or a land-on-self) reach the manager first.
        var ungrouped = section.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "group is null");
        var moves = section.Calls("_tabs.MoveGroup").OrderBy(m => m.Span.Start).ToList();
        var leftGuard = section.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "start <= _tabs.PinCount");
        var rightGuard = section.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "start + run.Count >= _tabs.Tabs.Count");

        Assert.True(ungrouped.Span.Start < moves[0].Span.Start);
        Assert.True(leftGuard.Span.Start < moves[0].Span.Start);
        Assert.True(moves[0].Span.Start < rightGuard.Span.Start);
        Assert.True(rightGuard.Span.Start < moves[1].Span.Start);
    }

    /// <summary>
    /// The host hands the tail tags to the pane-action arm, which is where
    /// ApprtActionMap lives: pin/unpin are payload-free, and move_group
    /// reads its signed offset from the payload at +8 like move_tab.
    /// Collapsing pin_tab to "is it MoveGroup" (the SetTitle/Tab mistake)
    /// would move a group when the user asked to pin.
    /// </summary>
    [Fact]
    public void Host_DispatchesTailTags_ThroughThePaneActionArm()
    {
        var host = Host();

        var pin = host.Case("OnAction", "GhosttyActionTag.PinTab");
        var pinDispatch = pin.Calls("DispatchPaneAction").Single();
        Assert.Equal("owner", pinDispatch.Arg(0));
        Assert.Equal("tag.Value", pinDispatch.Arg(1));
        Assert.Equal("0", pinDispatch.Arg(2));

        var move = host.Case("OnAction", "GhosttyActionTag.MoveGroup");
        Assert.Contains(
            move.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.CalleeText())
                .Where(t => t.Contains("ReadUnaligned<"))
                .ToList(),
            t => t.EndsWith("ReadUnaligned<GhosttyActionMoveGroup>"));
        var moveDispatch = move.Calls("DispatchPaneAction").Single();
        Assert.Equal("(int)mg.Amount", moveDispatch.Arg(2));
    }

    /// <summary>
    /// No curated default chord names the tab-shell verbs: the v1 ruling
    /// is that these are bindable, not bound, and the palette/context menu
    /// stay the primary paths. The defaults live compiled in Config.zig,
    /// so the fact reads that file; a curated default (or even a doc
    /// mention) is a deliberate product decision that has to delete this
    /// assertion to land.
    /// </summary>
    [Fact]
    public void TabShellVerbs_ShipNoDefaultChord()
    {
        const string resource = "Ghostty.Tests.Config.Defaults.Config.zig";
        var asm = typeof(ShellSource).Assembly;
        using var stream = asm.GetManifestResourceStream(resource);
        Assert.True(stream is not null, $"{resource} is not embedded; see Ghostty.Tests.csproj");
        using var reader = new System.IO.StreamReader(stream!);
        var config = reader.ReadToEnd();

        foreach (var verb in new[] { "pin_tab", "unpin_tab", "move_group" })
        {
            Assert.True(
                !config.Contains(verb, StringComparison.Ordinal),
                $"src/config/Config.zig mentions '{verb}'. The tab-shell verbs ship " +
                "with no default chord (owner decision, v1); a curated default or a " +
                "doc mention there means this no-default fact must be revisited first.");
        }
    }
}
