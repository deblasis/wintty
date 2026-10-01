using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Panes;
using Ghostty.Motion;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The close facts on <see cref="PaneTreeChange"/>, delivered: a soft
/// close reports undoable and soft on both of its records, a hard close
/// reports neither, and every other transition keeps reading with both
/// facts at their defaults.
///
/// This is the runtime half of the close-facts wiring. PaneHost is a
/// WinUI type no test host can construct, so the placement of the
/// emissions (the soft close's report before its fade, the tail's pair
/// around the rebuild, the fade standing down for a registered observer)
/// is pinned on the source by LifecycleHookWiringTests and
/// PaneFadeWiringTests; the emissions below mirror the guarded shape the
/// census pins, driven through the live registration exactly as the
/// sites run it. Neither half is a witness on its own.
/// </summary>
[Collection("PaneMotionSerial")]
public sealed class PaneCloseFactsDispatchTests
{
    private sealed class RecordingCoordinator : IPaneMotionCoordinator
    {
        public List<PaneTreeChange> Changing { get; } = new();
        public List<PaneTreeChange> Changed { get; } = new();

        public MotionPolicyLevel ResolvePolicy(MotionSurfaceClass surface) => MotionPolicyLevel.Off;
        public void OnPaneTreeChanging(PaneTreeChange change) => Changing.Add(change);
        public void OnPaneTreeChanged(PaneTreeChange change) => Changed.Add(change);
        public void OnFocusTransfer(FocusTransfer transfer) { }
        public void OnOverlayOpening(OverlayKind kind) { }
        public void OnOverlayDismissed(OverlayKind kind) { }
    }

    public PaneCloseFactsDispatchTests() => PaneMotion.ResetForTests();

    [Fact]
    public void ASoftClose_ReportsUndoableAndSoft_OnBothRecords()
    {
        // A real core pane-tree operation, so the ids the close reports
        // name real leaves on both sides of it.
        var active = new LeafPane();
        var fresh = new LeafPane();
        _ = PaneTree.Split(active, active, fresh, PaneOrientation.Vertical);

        var stub = new RecordingCoordinator();
        PaneMotion.Register(stub);

        // The soft close's emission as the shell runs it: the report at
        // the pre-visual point (before the fade arms), the commit after
        // the rebuild, both carrying the classifier's own values.
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(
                PaneTreeChangeKind.Close,
                BeforeLeafId: PaneMotionIds.Of(active),
                Undoable: true,
                SoftClose: true));
        }
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanged(new PaneTreeChange(
                PaneTreeChangeKind.Close,
                BeforeLeafId: PaneMotionIds.Of(active),
                AfterLeafId: PaneMotionIds.Of(fresh),
                Undoable: true,
                SoftClose: true));
        }

        var changing = Assert.Single(stub.Changing);
        Assert.True(changing.Undoable);
        Assert.True(changing.SoftClose);
        Assert.Equal(PaneMotionIds.Of(active), changing.BeforeLeafId);

        var changed = Assert.Single(stub.Changed);
        Assert.True(changed.Undoable);
        Assert.True(changed.SoftClose);
        Assert.Equal(PaneMotionIds.Of(active), changed.BeforeLeafId);
        Assert.Equal(PaneMotionIds.Of(fresh), changed.AfterLeafId);
    }

    [Fact]
    public void AHardClose_ReportsNeitherFact_OnBothRecords()
    {
        var active = new LeafPane();
        var fresh = new LeafPane();
        _ = PaneTree.Split(active, active, fresh, PaneOrientation.Vertical);

        var stub = new RecordingCoordinator();
        PaneMotion.Register(stub);

        // The hard close's emission: the tail's pair, with the facts the
        // classifier computed for a close that neither records for undo
        // nor retains the surface.
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(
                PaneTreeChangeKind.Close,
                BeforeLeafId: PaneMotionIds.Of(active),
                Undoable: false,
                SoftClose: false));
        }
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanged(new PaneTreeChange(
                PaneTreeChangeKind.Close,
                BeforeLeafId: PaneMotionIds.Of(active),
                AfterLeafId: PaneMotionIds.Of(fresh),
                Undoable: false,
                SoftClose: false));
        }

        var changing = Assert.Single(stub.Changing);
        Assert.False(changing.Undoable);
        Assert.False(changing.SoftClose);

        var changed = Assert.Single(stub.Changed);
        Assert.False(changed.Undoable);
        Assert.False(changed.SoftClose);
    }

    [Fact]
    public void OtherTransitions_KeepReadingWithBothFactsAtTheirDefaults()
    {
        var stub = new RecordingCoordinator();
        PaneMotion.Register(stub);

        // The records the other raisers build -- kind, ids, edge -- name
        // neither fact, and the record's defaults read false: every
        // raiser that predates the facts compiles and delivers unchanged.
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(PaneTreeChangeKind.Equalize));
            PaneMotion.Current!.OnPaneTreeChanged(new PaneTreeChange(PaneTreeChangeKind.Equalize));
            PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(
                PaneTreeChangeKind.Split, BeforeLeafId: PaneMotionIds.Of(new LeafPane())));
        }

        Assert.All(stub.Changing.Concat(stub.Changed), c =>
        {
            Assert.False(c.Undoable);
            Assert.False(c.SoftClose);
        });

        Assert.False(new PaneTreeChange(PaneTreeChangeKind.Zoom).Undoable);
        Assert.False(new PaneTreeChange(PaneTreeChangeKind.Zoom).SoftClose);
        Assert.False(new PaneTreeChange(
            PaneTreeChangeKind.Restore, OriginEdge: PaneEdge.None).SoftClose);
    }
}
