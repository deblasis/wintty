using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The deep-idle trim's wiring pins, hop by hop -- the same discipline as
/// the occlusion set, because the failure mode is the same shape: an
/// inverted or dropped hand-off compiles and silently trims nothing (or
/// trims the tab the user is looking at). The native import/export pair
/// is already enforced by EntryPointParityTests; these hold the managed
/// side together with the seam's dedupe latch.
/// </summary>
public class SurfaceIdleWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");
    private static ShellSource PaneHost() => ShellSource.Load("Panes.PaneHost.cs");
    private static ShellSource Native() => ShellSource.Load("Interop.Imports.NativeMethods.cs");
    private static ShellSource Tracker() => ShellSource.Load("Ghostty.Core.Tabs.TabIdleTracker.cs");

    [Fact]
    public void MainWindow_CarriesTheFlipAcrossTheSeam()
    {
        // The tracker's construction hands each flip to the tab's pane
        // host, un-negated: true must mean idle at every hop, because the
        // native side treats idle=true as "trim now".
        var call = Assert.Single(
            Window().Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "tab.PaneHost.SetSurfaceIdle"));
        Assert.Equal("idle", call.Arg(0));
    }

    [Fact]
    public void PaneHost_GuardsZeroHandles_AndPassesTheFlagThrough()
    {
        var method = PaneHost().Method("SetSurfaceIdle");
        var body = method.Body!;

        // Same guard-before-call shape as visibility: a surface that does
        // not exist yet (or is disposed) answers IntPtr.Zero, and the
        // recorded state reaches it at spawn instead.
        var guard = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("IntPtr.Zero")).ToList();
        var call = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "Interop.NativeMethods.SurfaceSetIdle").ToList();
        Assert.Single(guard);
        Assert.Single(call);
        Assert.True(guard[0].SpanStart < call[0].SpanStart,
            "the zero-handle guard must precede the native call");
        Assert.Equal("idle", call[0].Arg(1));
    }

    [Fact]
    public void LateSpawnedSurfacesInheritTheRecordedIdle()
    {
        var spawn = PaneHost().Method("OnLeafSurfaceSpawned");
        var reapply = spawn.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString() == "_surfaceIdle == true").ToList();
        Assert.Single(reapply);
        // The argument is the polarity: idle is what a late spawn missed,
        // and false would both skip the trim and flip the recorded field
        // so later spawns never apply it.
        var call = Assert.Single(
            reapply[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "SetSurfaceIdle"));
        Assert.Equal("true", call.Arg(0));
    }

    [Fact]
    public void Wrapper_MapsIdleTrueToByteOne()
    {
        // Exact argument text, not a substring: a negated condition
        // (`!idle ? (byte)1 : ...`) would survive a Contains and pin an
        // inverted wrapper.
        var method = Native().Method("SurfaceSetIdle");
        var native = Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "SurfaceSetIdleNative"));
        Assert.Equal("idle ? (byte)1 : (byte)0", native.Arg(1));
    }

    [Fact]
    public void TheSweepNotifiesOnlyOnEdges()
    {
        var sweep = Tracker().Method("Sweep");
        // The flip guard: a sweep that changes nothing must not notify,
        // or the trims re-fire every period.
        Assert.Contains(
            sweep.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString() == "idle == tab.IsIdle");
        // The notify follows the property write, so native state can
        // never lag the badge it is supposed to share an edge with.
        var write = Assert.Single(
            sweep.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == "tab.IsIdle"));
        // Exact callee: CalleeText reconstructs the null-conditional
        // receiver, so equality is available and a Contains would accept
        // any similarly-named call.
        var notify = Assert.Single(
            sweep.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "_onIdleFlip?.Invoke"));
        Assert.True(write.SpanStart < notify.SpanStart,
            "the property write precedes the flip notification");
    }
}
