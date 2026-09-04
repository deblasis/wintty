using System.Linq;
using Ghostty.Core.Tabs;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The idle indicator's wiring, pinned end to end. The state machine
/// itself is behaviour-tested against Ghostty.Core
/// (TabIdleTrackerTests); what these guards own is every hop the state
/// has to survive to reach pixels: the surface stamps its signal paths,
/// the pane host aggregates the leaves, both strips listen for the
/// property, the moon yields to the bell, and the window arms and
/// disposes the one sweep that writes the state.
/// </summary>
public class TabIdleWiringTests
{
    private static ShellSource Terminal() => ShellSource.Load("Controls.TerminalControl.xaml.cs");
    private static ShellSource PaneHost() => ShellSource.Load("Panes.PaneHost.cs");
    private static ShellSource Interface() => ShellSource.Load("Core.Panes.IPaneHost.cs");
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");
    private static ShellSource TabHost() => ShellSource.Load("Tabs.TabHost.xaml.cs");
    private static ShellSource NavRow() => ShellSource.Load("Tabs.VerticalTabNavRow.cs");
    private static ShellSource PinnedRow() => ShellSource.Load("Tabs.VerticalTabPinnedRow.cs");
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
    private static ShellSource Seam() => ShellSource.Load("Testing.TestSeam.cs");

    [Theory]
    [InlineData("OnKeyDown")]
    [InlineData("OnPointerPressed")]
    [InlineData("OnPointerWheelChanged")]
    [InlineData("QueueScrollbarChanged")]
    [InlineData("RaiseTitleChanged")]
    [InlineData("RaiseProgressChanged")]
    [InlineData("RaisePwdChanged")]
    [InlineData("RaiseBellRang")]
    public void TheSurfaceStampsEverySignalPath(string method)
    {
        // Each of these is a way a session receives data or interaction.
        // A stamp dropped from any one of them is a tab that dims while
        // it is visibly working -- the one failure mode users report.
        Assert.NotEmpty(Terminal().Method(method).Calls("NoteActivity"));
    }

    [Fact]
    public void ThePaneHostAggregatesTheLiveTree()
    {
        var property = PaneHost().Root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "LastActivityTick");

        // The getter walks the tree rather than reading a cached field:
        // a cache would go stale the day a leaf splits off or closes,
        // and the sweep would keep reading the dead leaf's stamp.
        Assert.Contains(
            property.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "PaneTree.Leaves");
        // And it refuses to walk a collapsed tree, the same teardown
        // rule the other tree walks in this class follow.
        Assert.Contains("_allLeavesClosed", property.ToString());

        // The hop the sweep actually takes: the interface member the
        // tracker reads through TabModel.PaneHost.
        Assert.Contains(
            Interface().Root.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.ValueText == "LastActivityTick");
    }

    [Fact]
    public void BothVerticalRowKindsListenForTheProperty()
    {
        // The vertical rows refresh through AotBinding property lists,
        // not the horizontal strip's PropertyChanged chain. A missing
        // property name here compiles green and the row simply never
        // hears the state change -- the classic blind wiring guard.
        var bindings = Strip().Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "AotBinding.Create"
                        && i.ArgumentList.Arguments
                            .Any(a => a.ToString() == "nameof(TabModel.IsIdle)"))
            .ToList();
        Assert.Equal(2, bindings.Count);

        // The collapsed rail is icon-only: the row's title, bell, and
        // moon are laid out past the rail's edge and clipped away, so at
        // rest the item's icon is the only carrier of the state. Without
        // this write the resting rail shows nothing for an idle tab.
        Assert.Contains(
            Strip().Root.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "icon.Opacity"
                 && a.Right.ToString() == "tab.IsIdle ? IdleIconOpacity : 1.0");
    }

    [Fact]
    public void TheHorizontalStripHasAnIsIdleArm()
    {
        var arm = TabHost().Method("AddItem").DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("nameof(TabModel.IsIdle)"))
            .ToList();
        Assert.Single(arm);
        // The arm does both halves of the visual: the moon and the dim.
        var text = arm[0].ToString();
        Assert.Contains("idleGlyph.Visibility", text);
        Assert.Contains("ApplyIdleInk", text);
    }

    [Fact]
    public void TheMoonYieldsToTheBellEverywhere()
    {
        // All three badge sites derive visibility from the same shape:
        // idle AND NOT ringing. Inverted polarity compiles and shows
        // both badges at once, or neither, so the expression itself is
        // pinned as parsed, not as a substring.
        foreach (var source in new[] { TabHost(), NavRow(), PinnedRow() })
        {
            var shaped = source.Root.DescendantNodes()
                .OfType<BinaryExpressionSyntax>()
                .Where(b => b.IsKind(SyntaxKind.LogicalAndExpression)
                            && b.Left.ToString() == "tab.IsIdle"
                            && b.Right.IsKind(SyntaxKind.LogicalNotExpression)
                            && b.Right.ToString() == "!tab.BellRinging")
                .ToList();
            Assert.NotEmpty(shaped);
        }
    }

    [Fact]
    public void TheInkClearPassRestoresTheMoonsQuietInk()
    {
        // The moon is born muted; the tab-colour clear pass would strand
        // it primary-coloured for life without its own case, exactly the
        // way the bell needed its accent restored.
        TabHost().Case("ClearHeaderRowForeground", "IdleGlyph");
    }

    [Fact]
    public void TheWindowArmsAndDisposesTheSweep()
    {
        var window = Window();
        Assert.Single(window.Root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "TabIdleTracker"));
        Assert.Contains(window.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "_idleTracker.Start");
        Assert.Contains(window.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "_idleTracker.Dispose");
    }

    [Fact]
    public void TheSeamDrivesAndReportsTheProperty()
    {
        var seam = Seam();
        var op = seam.Root.DescendantNodes()
            .OfType<SwitchSectionSyntax>()
            .Where(s => s.Labels.ToString().Contains("\"tab-idle\""))
            .ToList();
        Assert.Single(op);
        Assert.Contains(op[0].DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "tab.IsIdle");
        // The state dump answers with the value it read, so a scenario
        // can assert the round trip, not just the lack of an error.
        Assert.Contains("WriteBoolean(\"idle\"", seam.Root.ToString());
    }
}
