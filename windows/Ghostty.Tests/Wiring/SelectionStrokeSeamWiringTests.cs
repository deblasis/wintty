using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Since the field design (#918) the selected row's fill is the terminal's
/// own ground, and the active tab's whole separation from the strip around
/// it is one 1-DIP accent stroke on the row's closed sides. #931 is the
/// observation that nothing measured that stroke -- both oracle legs passed
/// with a happy 1.00:1 on a strip whose active tab was indistinguishable
/// from its neighbours.
///
/// The fix has two halves and these pins watch both. The strips expose the
/// stroke they are holding (TabSelectionStroke on ITabHost), read back off
/// the same properties the drawing path writes; and the seam's layout-frame
/// carries that answer for whichever host is mounted, placed by the
/// ClientToScreen path every capture rect uses. A 1-DIP band cannot be
/// re-derived by a harness from DIPs and a guessed window origin, so the
/// C#-side placement is not a convenience -- it is the difference between
/// measuring the stroke and scoring the fill against itself.
///
/// What these can catch: the vertical readout detached from the live
/// border, the horizontal readout's key drifting from the chrome pass's
/// push (so the seam reads a brush nothing pushes), the band reverting to
/// the padded item instead of the template's TabContainer, the stroke
/// push escaping its selected-only guard, layout-frame losing the stroke
/// for one host or gaining settle side effects mid-leg.
///
/// What they cannot catch: whether the pixels carry what the seam declares.
/// That is the live-window oracle's vtab-selection-stroke and
/// htab-selection-stroke surfaces, whose verdicts come out of a capture.
/// </summary>
public class SelectionStrokeSeamWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
    private static ShellSource TabHost() => ShellSource.Load("Tabs.TabHost.xaml.cs");
    private static ShellSource Seam() => ShellSource.Load("Testing.TestSeam.cs");

    /// <summary>
    /// The vertical readout must read the border UpdateSelectionRow writes.
    /// A getter that answers from a cached copy reports a stroke that is
    /// exactly as stale as the last thing that forgot to refresh it.
    /// </summary>
    [Fact]
    public void VerticalSeamStroke_ReadsTheLiveBorder()
    {
        var getter = Strip().Method("TestSeamSelectionStroke");
        var text = getter.ToString();

        Assert.True(
            text.Contains("SelectionRow.BorderBrush"),
            "the vertical stroke readout must read SelectionRow.BorderBrush: it is the "
            + "property UpdateSelectionRow writes, and a readout from anywhere else "
            + "reports a stroke the strip may not be drawing");
        Assert.True(
            text.Contains("SelectionRow.BorderThickness"),
            "the vertical stroke readout must read SelectionRow.BorderThickness: the "
            + "harness bands the stroked edges from these sides");
        Assert.True(
            text.Contains("SelectionRow.Visibility"),
            "the vertical stroke readout must gate on SelectionRow.Visibility: a "
            + "collapsed row draws no stroke, and reporting one would score whatever "
            + "the strip paints in its place");
    }

    /// <summary>
    /// The affordance exists only while UpdateSelectionRow keeps assigning
    /// the stroke: the brush comes off the tab's own palette when it has
    /// one and the accent when it has not, and the sides stay closed on
    /// three edges (the pane edge is the open one). Drop the assignment and
    /// the seam's shown=false is the honest answer -- this pin keeps that
    /// honest answer from becoming the only answer.
    /// </summary>
    [Fact]
    public void UpdateSelectionRow_StillAssignsTheStroke()
    {
        static IEnumerable<AssignmentExpressionSyntax> Assignments(
            SyntaxNode node, string target) =>
            node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == target);

        var body = Strip().Method("UpdateSelectionRow").Body!;

        var brush = Assignments(body, "SelectionRow.BorderBrush").ToList();
        Assert.True(
            brush.Count == 1,
            $"expected exactly one SelectionRow.BorderBrush assignment in "
            + $"UpdateSelectionRow, found {brush.Count}: the stroke is written in one "
            + "place so the seam readout and the drawing cannot drift apart");
        var conditional = brush[0].Right as ConditionalExpressionSyntax;
        Assert.True(
            conditional is not null,
            "the stroke brush must be the tab-colour-or-accent conditional: a "
            + "preset-coloured tab is stroked in its own border colour, matching its "
            + "pane");
        Assert.True(
            conditional.WhenFalse is IdentifierNameSyntax { Identifier.ValueText: "AccentBrush" },
            "the uncoloured arm must be AccentBrush: that stroke is the active tab's "
            + "separation from the strip, the affordance the oracle's "
            + "selection-stroke surfaces score");

        var thickness = Assignments(body, "SelectionRow.BorderThickness").ToList();
        Assert.True(
            thickness.Count == 1,
            "expected exactly one SelectionRow.BorderThickness assignment in "
            + "UpdateSelectionRow");
        Assert.True(
            thickness[0].Right is ConditionalExpressionSyntax,
            "the sides must stay the body-versus-square conditional: a body row leaves "
            + "the pane edge open, a pinned square is closed on all four");
    }

    /// <summary>
    /// The horizontal readout and the chrome pass must spell the resource
    /// key the same way. The push writes TabViewSelectedItemBorderBrush into
    /// the selected item's resources; the readout reads the same key back.
    /// One side renamed, the seam reads a brush nothing pushes and reports a
    /// stroke where the strip draws none.
    /// </summary>
    [Fact]
    public void HorizontalSeamStroke_UsesTheKeyTheChromePassPushes()
    {
        const string key = "TabViewSelectedItemBorderBrush";
        var host = TabHost();

        var pushes = host.Method("ApplyTabChrome").Body!.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "SetItemHeaderBrush" && c.Arg(1) == $"\"{key}\"")
            .ToList();
        Assert.True(
            pushes.Count == 1,
            $"expected exactly one SetItemHeaderBrush push of {key} in ApplyTabChrome, "
            + $"found {pushes.Count}");

        var getter = host.Method("TestSeamSelectionStroke").ToString();
        Assert.Contains($"\"{key}\"", getter);

        // The band is the template's TabContainer (the stroke-carrying
        // element the template themes from the pushed key), not the item:
        // the item is padded well past the stroke, and a band taken off the
        // item samples that padding and scores the stroke against itself.
        Assert.Contains("FindDescendantByName", getter);
        Assert.Contains("\"TabContainer\"", getter);
    }

    /// <summary>
    /// The brush the chrome pass pushes for the border key is computed only
    /// for the selected item. The push itself always runs -- it writes null
    /// for an unselected item, which REMOVES the key so the template falls
    /// back to its transparent default -- so the guard to pin is upstream:
    /// the variable reaching the push is assigned only inside the selected
    /// conditional. A brush computed unconditionally would dress every item
    /// in the selected stroke the day a template consults the key while
    /// unselected.
    /// </summary>
    [Fact]
    public void HorizontalChrome_ComputesTheStrokeOnlyForTheSelectedItem()
    {
        var method = TabHost().Method("ApplyTabChrome").Body!;
        var push = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.CalleeText() == "SetItemHeaderBrush"
                        && c.Arg(1) == "\"TabViewSelectedItemBorderBrush\"");
        var carried = Assert.IsType<IdentifierNameSyntax>(push.ArgExpression(2));

        var assigns = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id
                        && id.Identifier.ValueText == carried.Identifier.ValueText)
            .ToList();
        Assert.True(
            assigns.Count >= 1,
            $"nothing assigns '{carried.Identifier.ValueText}' before the push");
        foreach (var assign in assigns)
        {
            var guard = assign.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
            Assert.True(
                guard is not null
                && guard.Condition.ToString().Contains("selected"),
                $"'{carried.Identifier.ValueText}' must be assigned only inside the "
                + "selected conditional: the stroke identifies THE selected tab");
        }
    }

    /// <summary>
    /// layout-frame carries the stroke for BOTH hosts: the oracle measures
    /// each layout against its own host's answer, and a frame that reports
    /// only the mounted one would let the unmeasured half pass by absence.
    /// The write must come from the host's own readout -- a frame that
    /// answers a constant passes every harness that trusts it.
    /// </summary>
    [Fact]
    public void TheLayoutFrameCarriesTheStrokeForBothHosts()
    {
        var seam = Seam();
        var frame = seam.Method("LayoutFrameJson").DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "WriteHost")
            .ToList();
        Assert.True(
            frame.Count == 2,
            $"expected layout-frame to report both hosts, found {frame.Count} WriteHost calls");
        Assert.Equal("\"horizontal\"", frame[0].Arg(1));
        Assert.Equal("\"vertical\"", frame[1].Arg(1));

        var writer = seam.Method("WriteSelectionStroke").Body!.ToString();
        Assert.Contains("TestSeamSelectionStroke", writer);
        Assert.Contains("TestSeamToScreenPixels", writer);
    }

    /// <summary>
    /// The oracle asks for a layout-frame in the middle of a leg, right
    /// beside a capture. That is only safe while layout-frame stays an
    /// observer op: a fetch that forces a settle pass would re-place the
    /// row between the capture and the frame, and the two would disagree
    /// about where the stroke was.
    /// </summary>
    [Fact]
    public void LayoutFrame_StaysAnObserverOp()
    {
        var body = Seam().Method("IsObserver").ToString();
        Assert.Contains("\"layout-frame\"", body);
    }
}
