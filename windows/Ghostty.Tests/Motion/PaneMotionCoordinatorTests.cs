using System;
using System.Collections.Generic;
using Ghostty.Motion;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// Unit tests for the <see cref="IPaneMotionCoordinator"/> registration
/// contract exercised through <see cref="PaneMotion"/>: the default is
/// no coordinator at all, registration is set-once and thread-safe, and
/// a registered observer receives the lifecycle notifications. Pure
/// in-process state, no UI thread.
/// </summary>
[Collection("PaneMotionSerial")]
public sealed class PaneMotionCoordinatorTests
{
    private sealed class RecordingCoordinator : IPaneMotionCoordinator
    {
        public MotionPolicyLevel Level { get; set; } = MotionPolicyLevel.Off;

        public List<MotionSurfaceClass> PolicyRequests { get; } = new();
        public List<PaneTreeChange> TreeChanging { get; } = new();
        public List<PaneTreeChange> TreeChanged { get; } = new();
        public List<FocusTransfer> FocusTransfers { get; } = new();
        public List<OverlayKind> OverlaysOpening { get; } = new();
        public List<OverlayKind> OverlaysDismissed { get; } = new();

        public MotionPolicyLevel ResolvePolicy(MotionSurfaceClass surface)
        {
            PolicyRequests.Add(surface);
            return Level;
        }

        public void OnPaneTreeChanging(PaneTreeChange change) => TreeChanging.Add(change);

        public void OnPaneTreeChanged(PaneTreeChange change) => TreeChanged.Add(change);

        public void OnFocusTransfer(FocusTransfer transfer) => FocusTransfers.Add(transfer);

        public void OnOverlayOpening(OverlayKind kind) => OverlaysOpening.Add(kind);

        public void OnOverlayDismissed(OverlayKind kind) => OverlaysDismissed.Add(kind);
    }

    public PaneMotionCoordinatorTests() => PaneMotion.ResetForTests();

    // Default state ----------------------------------------------------

    [Fact]
    public void Unregistered_Surface_IsInactive()
    {
        Assert.Null(PaneMotion.Current);
        Assert.False(PaneMotion.Active);
    }

    // Register(null) ---------------------------------------------------

    [Fact]
    public void Register_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PaneMotion.Register(null!));
    }

    // Double register --------------------------------------------------

    [Fact]
    public void Register_SecondCoordinator_ThrowsInvalidOperationException()
    {
        var first = new RecordingCoordinator();
        var second = new RecordingCoordinator();

        PaneMotion.Register(first);

        Assert.Throws<InvalidOperationException>(() => PaneMotion.Register(second));

        // The first registration is untouched by the refused one.
        Assert.Same(first, PaneMotion.Current);
    }

    // Notification delivery --------------------------------------------

    [Fact]
    public void Registered_StubReceivesCalls()
    {
        var stub = new RecordingCoordinator();

        PaneMotion.Register(stub);

        Assert.True(PaneMotion.Active);
        Assert.Same(stub, PaneMotion.Current);

        var treeChange = new PaneTreeChange(
            PaneTreeChangeKind.Split, BeforeLeafId: 3, AfterLeafId: 7, OriginEdge: PaneEdge.Left);
        var focusTransfer = new FocusTransfer(FromLeafId: 3, ToLeafId: 7);

        stub.Level = MotionPolicyLevel.Full;
        Assert.Equal(MotionPolicyLevel.Full, stub.ResolvePolicy(MotionSurfaceClass.PaneGeometry));

        PaneMotion.Current!.OnPaneTreeChanging(treeChange);
        PaneMotion.Current.OnPaneTreeChanged(treeChange);
        PaneMotion.Current.OnFocusTransfer(focusTransfer);
        PaneMotion.Current.OnOverlayOpening(OverlayKind.CommandPalette);
        PaneMotion.Current.OnOverlayDismissed(OverlayKind.QuickTerminal);

        var coordinator = Assert.IsType<RecordingCoordinator>(PaneMotion.Current);
        Assert.Equal(new[] { MotionSurfaceClass.PaneGeometry }, coordinator.PolicyRequests);
        Assert.Equal(new[] { treeChange }, coordinator.TreeChanging);
        Assert.Equal(new[] { treeChange }, coordinator.TreeChanged);
        Assert.Equal(new[] { focusTransfer }, coordinator.FocusTransfers);
        Assert.Equal(new[] { OverlayKind.CommandPalette }, coordinator.OverlaysOpening);
        Assert.Equal(new[] { OverlayKind.QuickTerminal }, coordinator.OverlaysDismissed);
    }
}
