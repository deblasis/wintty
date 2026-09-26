using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Panes;
using Ghostty.Motion;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The runtime half of the pane lifecycle wiring: with a coordinator
/// registered, one pane-tree operation's reports arrive as exactly one
/// Changing/Changed pair, in order, carrying ids that survive the
/// operation; with none registered, the guard every call site branches on
/// reads false and nothing is delivered.
///
/// What this can and cannot see: PaneHost, MainWindow and
/// LayoutCoordinator are WinUI types and cannot be constructed in a test
/// host, so the placement of the calls (Changing before the mutation,
/// Changed after the rebuild) is pinned on the source by
/// LifecycleHookWiringTests. This file exercises everything that IS
/// executable here -- the registration the sites consult, the dispatch
/// they call, the records they build, and the id seam they name -- driven
/// by the real core pane-tree operation the sites wrap, so a broken
/// surface reds here while a moved or unguarded call site reds there.
/// Neither file is a witness on its own.
/// </summary>
public sealed class PaneMotionLifecycleDispatchTests
{
    private sealed class RecordingCoordinator : IPaneMotionCoordinator
    {
        // Every delivery in arrival order, so the in-order claim is one
        // assert over one sequence rather than two collections compared
        // by index.
        public List<(string Edge, PaneTreeChange Change)> Tree { get; } = new();
        public List<FocusTransfer> Focus { get; } = new();

        public MotionPolicyLevel ResolvePolicy(MotionSurfaceClass surface) => MotionPolicyLevel.Off;
        public void OnPaneTreeChanging(PaneTreeChange change) => Tree.Add(("Changing", change));
        public void OnPaneTreeChanged(PaneTreeChange change) => Tree.Add(("Changed", change));
        public void OnFocusTransfer(FocusTransfer transfer) => Focus.Add(transfer);
        public void OnOverlayOpening(OverlayKind kind) { }
        public void OnOverlayDismissed(OverlayKind kind) { }
    }

    public PaneMotionLifecycleDispatchTests() => PaneMotion.ResetForTests();

    [Fact]
    public void OneSplit_ReportsExactlyOneChangingChangedPair_InOrder()
    {
        // A real core pane-tree operation, the one PaneHost.Split wraps:
        // its leaves are the pre-state and the post-state the sites
        // report.
        var active = new LeafPane();
        var fresh = new LeafPane();
        _ = PaneTree.Split(active, active, fresh, PaneOrientation.Vertical);

        var stub = new RecordingCoordinator();
        PaneMotion.Register(stub);

        // The guarded emission exactly as the census pins it at the site:
        // the guard is the condition, the record is built inside it.
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(
                PaneTreeChangeKind.Split, BeforeLeafId: PaneMotionIds.Of(active)));
        }
        if (PaneMotion.Active)
        {
            PaneMotion.Current!.OnPaneTreeChanged(new PaneTreeChange(
                PaneTreeChangeKind.Split,
                BeforeLeafId: PaneMotionIds.Of(active),
                AfterLeafId: PaneMotionIds.Of(fresh)));
        }

        Assert.Equal(
            new[] { "Changing", "Changed" },
            stub.Tree.Select(t => t.Edge).ToArray());

        var changing = Assert.Single(stub.Tree, t => t.Edge == "Changing").Change;
        var changed = Assert.Single(stub.Tree, t => t.Edge == "Changed").Change;
        Assert.Equal(PaneTreeChangeKind.Split, changing.Kind);
        Assert.Equal(PaneMotionIds.Of(active), changing.BeforeLeafId);
        Assert.Equal(PaneTreeChangeKind.Split, changed.Kind);
        Assert.Equal(PaneMotionIds.Of(active), changed.BeforeLeafId);
        Assert.Equal(PaneMotionIds.Of(fresh), changed.AfterLeafId);
        Assert.Empty(stub.Focus);
    }

    [Fact]
    public void LeafIds_SurviveTheOperation_AndStayDistinctPerPane()
    {
        var active = new LeafPane();
        var fresh = new LeafPane();
        var beforeActive = PaneMotionIds.Of(active);

        _ = PaneTree.Split(active, active, fresh, PaneOrientation.Vertical);

        // The pane that started the split kept its id through it; the pane
        // it gained is a different id. This is the property an observer
        // needs to tell "the same pane survived the rebuild" from "the
        // tree was replaced around me".
        Assert.Equal(beforeActive, PaneMotionIds.Of(active));
        Assert.NotEqual(beforeActive, PaneMotionIds.Of(fresh));

        // And a later leaf never collides with an earlier one: ids are
        // handed out once per leaf, never reused.
        Assert.NotEqual(PaneMotionIds.Of(active), PaneMotionIds.Of(new LeafPane()));
    }

    [Fact]
    public void InactiveByDefault_TheGuardEverySiteBranchesOn_ReadsFalse()
    {
        Assert.False(PaneMotion.Active);
        Assert.Null(PaneMotion.Current);

        // Registration is the only thing that flips the guard; clearing
        // it (the isolation every fact here runs under) flips it back.
        PaneMotion.Register(new RecordingCoordinator());
        Assert.True(PaneMotion.Active);
        PaneMotion.ResetForTests();
        Assert.False(PaneMotion.Active);
    }
}
