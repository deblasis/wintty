using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The occlusion wiring's polarity is the whole feature: the native
/// parameter means VISIBILITY (the C entry point spells it "occlusion"
/// but embedded.zig names it `visible`), so every hand-off has to keep
/// the sense straight or hiding a tab renders it and showing one parks
/// it. These pins walk the three hops -- the wrapper's byte mapping, the
/// PaneHost guard, and SwapActivePane's argument -- because each is one
/// edit away from inverting silently.
/// </summary>
public class SurfaceOcclusionWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");
    private static ShellSource PaneHost() => ShellSource.Load("Panes.PaneHost.cs");
    private static ShellSource Native() => ShellSource.Load("Interop.Imports.NativeMethods.cs");

    [Fact]
    public void SwapActivePane_OccludesExactlyTheHiddenHosts()
    {
        var swap = Window().Method("SwapActivePane");

        // Receiver included: a call on anything else would match a bare
        // name and pin the wrong wiring.
        var calls = swap.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "host.SetSurfaceVisibility")
            .ToList();
        Assert.Single(calls);

        // The ARGUMENT is the polarity: isActive, not !isActive. A guard
        // that only checks the call exists would pass an inverted hand-off
        // that renders hidden tabs and parks the visible one.
        Assert.Equal("isActive", calls[0].ArgumentList.Arguments[0].ToString());
    }

    [Fact]
    public void PaneHost_GuardsZeroHandles_AndPassesTheFlagThrough()
    {
        var method = PaneHost().Method("SetSurfaceVisibility");
        var body = method.Body!;

        // The zero-handle guard must precede the native call: surfaces
        // read IntPtr.Zero before TerminalControl loads and after it is
        // disposed, and the unguarded call AV'd at startup. Both live
        // inside the leaf loop, so this is a span comparison, not a
        // top-level statement index.
        var guard = body.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("IntPtr.Zero"))
            .ToList();
        var call = body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "Interop.NativeMethods.SurfaceSetVisible")
            .ToList();
        Assert.Single(guard);
        Assert.Single(call);
        Assert.True(
            guard[0].SpanStart < call[0].SpanStart,
            "the zero-handle guard must precede the native call");

        // The parameter flows through unchanged: the method's own `visible`
        // is what the wrapper receives, not a negation of it.
        Assert.Equal("visible", call[0].ArgumentList.Arguments[1].ToString());
    }

    [Fact]
    public void Wrapper_MapsVisibleTrueToByteOne()
    {
        // The byte mapping is where the C API's parameter meets ours: true
        // must become 1, which embedded.zig reads as visible=true. An
        // inverted mapping compiles and hides every visible surface.
        var method = Native().Method("SurfaceSetVisible");
        Assert.Contains(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.ToString().Contains("visible ? (byte)1 : (byte)0"));
    }

    [Fact]
    public void LateSpawnedSurfacesInheritTheRecordedVisibility()
    {
        // A restored background tab's surfaces spawn after SwapActivePane
        // already ran, so the zero-handle guard skipped them. The recorded
        // state must be written on every tell and re-applied at spawn --
        // and only for hidden, since visible is the native default a
        // spawned surface already has.
        var tell = PaneHost().Method("SetSurfaceVisibility");
        Assert.Contains(
            tell.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_surfaceVisibility"
                 && a.Right.ToString() == "visible");

        var spawn = PaneHost().Method("OnLeafSurfaceSpawned");
        var reapply = spawn.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString() == "_surfaceVisibility == false")
            .ToList();
        Assert.Single(reapply);
        // The re-apply's argument is the polarity: hidden is what a
        // late spawn missed, and true would both invert the fix and
        // flip the recorded field so later spawns skip entirely.
        var call = Assert.Single(
            reapply[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "SetSurfaceVisibility"));
        Assert.Equal("false", call.Arg(0));
    }
}
