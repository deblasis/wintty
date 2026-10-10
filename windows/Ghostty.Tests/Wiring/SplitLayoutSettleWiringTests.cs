using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The split-under-a-standing-flight kill loss: PaneHost.Split's splice
/// removes and re-adds the divided pane's element, and the motion
/// adapters' geometry sweep - which kills a standing reveal flight on
/// that pane at its new rect and recommits from the held insets - rides
/// a LayoutUpdated tick. Between Split's return and the framework's own
/// async layout pass the dispatcher delivers the reparent's queued
/// Unloaded, which retires the divided pane's standing flight before any
/// sweep can kill it - measured as the geometry arc answering kills=0
/// with the flight pruned silently (a split landing inside the first
/// frame of the previous birth loses the kill every time; a paced one
/// loses it to the same race), while the same arc paced one pipe round
/// trip later kills within milliseconds.
///
/// What is only observable here is the settlement's shape, both halves
/// of it: Split runs one synchronous layout pass directly after the
/// splice (never inside the deferred-focus lambda - a deferred settle is
/// the async pass again and the race reopens), while the tree-change
/// raise and the deferred focus are still ahead of it, so the sweep the
/// settle drives observes the post-splice geometry while everything that
/// arms ON the raise (the newborn's reveal resolve, first-layout surface
/// creation through the newborn's Loaded) keeps the async rhythm; and
/// the settle re-invalidates measure so that async pass still comes -
/// consuming the splice's own invalidation without replacing it leaves
/// the newborn's creation waiting on a pass that never arrives until the
/// 250 ms fallback, which the birth floor reads as a dead pane.
/// </summary>
public class SplitLayoutSettleWiringTests
{
    private static MethodDeclarationSyntax SplitBody()
    {
        // PaneHost.cs carries two Split overloads; the one with a block
        // body is the real sequence (the other delegates to it).
        var split = ShellSource.Load("Panes.PaneHost.cs")
            .Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "Split" && m.Body is not null)
            .ToList();
        return Assert.Single(split);
    }

    /// <summary>
    /// The root-cause pin: exactly one synchronous settle, directly in
    /// the method body, after the splice's final child add (the geometry
    /// the sweep reads is the post-splice one) and before the tree-change
    /// raise leaves the stack (the newborn's resolve keeps its async
    /// pass).
    /// </summary>
    [Fact]
    public void Split_SettlesLayoutSynchronouslyBetweenSpliceAndTreeChange()
    {
        var body = SplitBody().Body!;

        var settle = Assert.Single(body.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "UpdateLayout");
        var adds = body.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("Children.Add"))
            .ToList();
        var spliceAdd = Assert.Single(adds);
        var raise = Assert.Single(body.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "RaiseLayoutChanged");

        Assert.True(spliceAdd.SpanStart < settle.SpanStart,
            "the settle must follow the splice, or the sweep it drives reads the pre-split geometry");
        Assert.True(settle.SpanStart < raise.SpanStart,
            "the settle must precede the tree-change raise, or the newborn's reveal resolve loses its async pass");

        Assert.DoesNotContain(
            body.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>(),
            a => a.Span.Contains(settle.Span));
    }

    /// <summary>
    /// The settle must hand the async rhythm back: one measure
    /// invalidation directly after it, so the pass the newborn's Loaded
    /// and the tree-change raise arm against still runs. Losing it is
    /// the dead newborn the birth floor reads (creation waits on the
    /// 250 ms fallback instead of the next tick).
    /// </summary>
    [Fact]
    public void Split_ReinvalidatesMeasureAfterTheSettle()
    {
        var body = SplitBody().Body!;

        var settle = Assert.Single(body.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "UpdateLayout");
        var invalidate = Assert.Single(body.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "InvalidateMeasure");

        Assert.True(settle.SpanStart < invalidate.SpanStart,
            "the measure invalidation is the settle's other half: without it the async pass never comes");
    }
}
