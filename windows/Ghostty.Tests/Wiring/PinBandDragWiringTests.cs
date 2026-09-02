using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The band drag's arbitration boundary, and the one property that has to
/// hold across both engines that read it.
///
/// A wrapping band and a linear list share one gesture, and they are split
/// on the machine's own axis: above the shelf's bottom the band answers,
/// below it the list does. That edge is decided mid-drag, and it is acted
/// on -- an arriving row is PINNED the moment the band claims a tick. So
/// the release cannot ask a different question. If it reads a different
/// edge, every pointer in the gap between the two edges is a row the drag
/// pinned and the release unpinned again, which is not a rounding error
/// but a drop landing somewhere the user never aimed.
///
/// The specific trap this pins shut: the band panel is inset INSIDE the
/// shelf, so its rect and the shelf's rect differ by exactly the panel's
/// top and bottom margins. Reading the panel here looks equivalent and is
/// not. Wiring guards, not behaviour tests -- which edge a live release
/// falls on is only observable on an arranged strip.
/// </summary>
public class PinBandDragWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    /// <summary>
    /// One edge, both readers. The arbitration that decides to pin and the
    /// release that decides to keep must call the same helper, or they can
    /// disagree about the same pointer.
    /// </summary>
    [Fact]
    public void TheBandAndTheRelease_ReadTheSameEdge()
    {
        // THREE readers, not two. The arbitration decides to pin, the ghost
        // promises where, and the release decides to keep -- and the ghost
        // was the one that got missed the first time round, gated on the
        // dragged row's centre while the other two read the pointer. They
        // differ by the grab offset, so there was a band of positions where
        // the drag pinned a row the preview had refused to promise.
        foreach (var method in new[] { "BandTargetSlot", "UpdatePinPreview", "DragRelease" })
            Assert.Single(Strip().Method(method).Calls("ShelfBottomY"));

        // ...and the two that REFUSE do it on the same side of the same
        // number, asserted as a tree. `>=` and `>` are both
        // BinaryExpressionSyntax, so a substring cannot tell "at the edge
        // the band answers" from "at the edge it declines" -- and that one
        // pixel row is a strip where the drag pins and the release unpins,
        // which is the disagreement this whole file exists to close.
        foreach (var method in new[] { "BandTargetSlot", "UpdatePinPreview" })
        {
            var refusal = Strip().Method(method)
                .DescendantNodes().OfType<BinaryExpressionSyntax>()
                .Single(b => b.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                    && b.Right.ToString() == "shelfBottom");

            Assert.Equal("drag.LastPointerY", refusal.Left.ToString());
        }
    }

    /// <summary>
    /// And the release reads it from nowhere else. The panel's own rect is
    /// the near-miss that was there before: same idea, inset by the
    /// margins, so it disagreed with the arbitration exactly across the
    /// two strips the band claims and the panel does not cover.
    /// </summary>
    [Fact]
    public void TheRelease_NeverMeasuresTheBandPanel()
    {
        Assert.DoesNotContain(
            Strip().Method("DragRelease").DescendantNodes().OfType<IdentifierNameSyntax>(),
            id => id.Identifier.ValueText == "_pinnedPanel");
    }

    /// <summary>
    /// The harness has to be able to SEE a pin, or a green leg and a red
    /// one look alike. A drag into the band changes the tab's pin state and
    /// the release can change it back, so the pin is half of what the
    /// gesture produced -- but it is carried on a field that defaults to
    /// false, and a path that forgets to set it reports a clean "not
    /// pinned" rather than failing. That is the worst shape a harness can
    /// have: `drag` skipped it, and the band leg read the default as a
    /// product bug.
    ///
    /// The count is pinned deliberately. Without it a sweep like this goes
    /// quiet the moment a new drag op is added -- which is exactly when the
    /// omission would be made again.
    /// </summary>
    [Fact]
    public void EverySeamDragPath_ReportsThePinState()
    {
        var drags = Strip().Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText.StartsWith(
                "TestSeamDrag", StringComparison.Ordinal))
            .Where(m => m.ReturnType.ToString().Contains(
                "TestSeamDragOutcome", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(5, drags.Count);

        foreach (var drag in drags)
        {
            // The receiver is pinned too, not just the member name: any
            // member called Pinned would otherwise satisfy this, and the
            // one that matters is the outcome the seam serializes.
            var report = drag.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .SingleOrDefault(a => a.Left is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.ValueText == "Pinned"
                    && member.Expression is IdentifierNameSyntax receiver
                    && receiver.Identifier.ValueText == "outcome");

            Assert.True(report is not null,
                $"{drag.Identifier.ValueText} never assigns outcome.Pinned, so it "
                + "reports every drop as unpinned");

            // WHAT it assigns, not merely that it assigns. `outcome.Pinned
            // = false` would satisfy an existence check while reproducing
            // the exact defect this test was written for: a default read
            // back as a measurement.
            Assert.Equal("tab.IsPinned", report!.Right.ToString());

            // ...and WHEN. The release is what classifies pin-out, so a
            // report taken before it measures the gesture's aim rather
            // than its result -- green test, wrong answer.
            var release = drag.Calls("DragRelease").Single();
            Assert.True(
                report.SpanStart > release.SpanStart,
                $"{drag.Identifier.ValueText} reports outcome.Pinned before "
                + "DragRelease, so it records the pre-release state");
        }
    }
}
