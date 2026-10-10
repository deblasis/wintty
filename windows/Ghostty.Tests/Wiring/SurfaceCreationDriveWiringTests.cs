using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The split-burst dead pane: a newborn leaf whose surface creation was
/// never driven. WinUI 3 delivers a reparent's pending Unloaded AFTER the
/// re-add's Loaded when the splice removes and re-adds a control in one
/// call stack (observed in a traced split burst as Loaded then Unloaded
/// 2 ms apart, with no further Loaded ever arriving for that pane), so
/// any creation mechanism Unloaded disarms dies there while the pane
/// stays attached and measured: the seam's surface-state readback reports
/// attempted=false retriesLeft=-1 with a nonzero panel, forever, and no
/// retry is armed because a never-attempted creation never failed.
///
/// What is only observable here is that the two defenses stay bolted
/// together: the LayoutUpdated subscription survives the reparent, and
/// the Loaded fallback settles creation itself instead of trusting the
/// forced pass to raise an event on an unchanged size.
/// </summary>
public class SurfaceCreationDriveWiringTests
{
    private static ShellSource Terminal() =>
        ShellSource.Load("Controls.TerminalControl.xaml.cs");

    private static IEnumerable<AssignmentExpressionSyntax> LayoutUpdatedRemovals(
        MethodDeclarationSyntax method) =>
        method.Body!.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                        && a.Left.ToString() == "Panel.LayoutUpdated");

    /// <summary>
    /// The root-cause pin: OnUnloaded must not remove the creation
    /// subscription. With the unsubscribe in place, a pane uncreated at
    /// the moment the late Unloaded lands ends the reparent attached,
    /// measured and unheard -- no layout pass, not even the fallback's
    /// forced one, can drive its creation. The unsubscribe was the whole
    /// defect; it must not come back.
    /// </summary>
    [Fact]
    public void OnUnloaded_KeepsTheCreationSubscriptionArmed()
    {
        Assert.Empty(LayoutUpdatedRemovals(Terminal().Method("OnUnloaded")));
    }

    /// <summary>
    /// The fallback's forced measure is not a driver on its own: it
    /// delivers creation only through the armed LayoutUpdated handler,
    /// and a re-arrange at an unchanged size may not raise that event
    /// for this panel at all. The fallback therefore settles the
    /// creation itself, reading the arranged size the retry timer
    /// already trusts -- after the suppress flag and the forced measure,
    /// so the creation it drives neither steals focus nor reads a
    /// measure-phase ghost.
    /// </summary>
    [Fact]
    public void LoadedFallback_SettlesCreationItself()
    {
        var loaded = Terminal().Method("OnLoaded").Body!;

        var settle = Assert.Single(loaded.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "TrySettleSurfaceCreation");
        var invalidate = Assert.Single(loaded.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "Panel.InvalidateMeasure");
        var suppress = Assert.Single(loaded.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_suppressCreationAutoFocus");

        Assert.True(suppress.SpanStart < invalidate.SpanStart,
            "the focus suppress must be set before the forced measure");
        Assert.True(invalidate.SpanStart < settle.SpanStart,
            "the settle must come after the forced measure");
    }

    /// <summary>
    /// The handler is one-shot in EFFECT, not in subscription, and the
    /// exits are what make that safe: creation success retires it, the
    /// first settled pass retires it, teardown retires it, and OnLoaded
    /// re-arms it idempotently so a reparent's size push still lands.
    /// Losing an exit leaks the handler past dispose; losing the re-arm
    /// strands a reparented pane's size push.
    /// </summary>
    [Fact]
    public void CreationSubscription_EndsAtSuccessAndDispose_AndRearmsOnLoad()
    {
        var terminal = Terminal();

        Assert.NotEmpty(LayoutUpdatedRemovals(terminal.Method("TrySettleSurfaceCreation")));
        Assert.NotEmpty(LayoutUpdatedRemovals(terminal.Method("OnFirstLayoutUpdated")));
        Assert.NotEmpty(LayoutUpdatedRemovals(terminal.Method("DisposeSurface")));

        Assert.Contains(terminal.Method("OnLoaded").Body!.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                 && a.Left.ToString() == "Panel.LayoutUpdated");
    }

    /// <summary>
    /// The removal rule is WHOLE-FILE: a regression that relocates the
    /// unsubscribe out of OnUnloaded (a helper OnUnloaded calls, a detach
    /// handler, OnSizeChanged) re-arms the dead-pane defect while every
    /// method-scoped assert above stays green. The creation subscription
    /// may be removed in exactly four shapes: the settle (success and the
    /// first settled pass), the layout handler itself, teardown, and
    /// OnLoaded's idempotent RE-ARM - which this fact also proves is a
    /// re-arm (the add follows the removal in the same body), never a
    /// bare disarm.
    /// </summary>
    [Fact]
    public void CreationSubscription_IsRemovedNowhereElseInTheFile()
    {
        var terminal = Terminal();
        var permitted = new HashSet<string>
        {
            "TrySettleSurfaceCreation",
            "OnFirstLayoutUpdated",
            "DisposeSurface",
            "OnLoaded",
        };

        var offenders = terminal.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body is not null && !permitted.Contains(m.Identifier.ValueText))
            .SelectMany(m => LayoutUpdatedRemovals(m).Select(r => (Method: m.Identifier.ValueText, Site: r)))
            .ToList();

        Assert.Empty(offenders.Select(o => $"{o.Method}: {o.Site}"));

        // OnLoaded's permitted removal must be the re-arm: exactly one
        // removal and one add, removal first, both in the same body (one
        // call stack on the UI thread, so no layout can observe the gap).
        var loaded = Terminal().Method("OnLoaded").Body!;
        var removal = Assert.Single(LayoutUpdatedRemovals(Terminal().Method("OnLoaded")));
        var add = Assert.Single(loaded.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                 && a.Left.ToString() == "Panel.LayoutUpdated");
        Assert.True(removal.SpanStart < add.SpanStart,
            "OnLoaded's removal is only legal as the first half of the re-arm");
    }
}
