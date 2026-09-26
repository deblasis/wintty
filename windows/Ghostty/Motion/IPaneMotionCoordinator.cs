namespace Ghostty.Motion;

/// <summary>
/// How much motion a surface may run: the full effect, a reduced variant
/// (nothing that moves, scales or travels), or none at all. Reported per
/// <see cref="MotionSurfaceClass"/> so different surfaces can sit on
/// different dials.
/// </summary>
internal enum MotionPolicyLevel { Full, Reduced, Off }

/// <summary>
/// The family a moving surface belongs to. Callers ask
/// <see cref="IPaneMotionCoordinator.ResolvePolicy"/> with the class that
/// describes what they are about to run, so pane geometry, window chrome,
/// overlays and ambient effects can each resolve to their own
/// <see cref="MotionPolicyLevel"/>.
/// </summary>
internal enum MotionSurfaceClass { PaneGeometry, Chrome, Overlay, Ambient }

/// <summary>
/// The edge a pane-tree change is anchored to, when one applies: the
/// divider a split or resize started from, or the dock side an attach
/// landed against. <see cref="None"/> when the change has no directional
/// origin (a close, a zoom, a bare reorder).
/// </summary>
internal enum PaneEdge { None, Left, Right, Top, Bottom }

/// <summary>
/// Which structural operation a <see cref="PaneTreeChange"/> reports.
/// The first five mirror the pure tree operations (<c>PaneOpKind</c>);
/// Move, Attach, Detach and Restore describe window-level tree
/// transitions (reorders, cross-window moves, and snapshot restores)
/// that rebuild the tree the same way a split does.
/// </summary>
internal enum PaneTreeChangeKind { Split, Close, Equalize, Resize, Zoom, Move, Attach, Detach, Restore }

/// <summary>
/// Which overlay surface is opening or dismissing.
/// </summary>
internal enum OverlayKind { CommandPalette, ResizeOverlay, SearchBar, QuickTerminal }

/// <summary>
/// One pane-tree transition. Leaf ids are opaque integers assigned by the
/// reporting code; they carry no meaning beyond one transition, where the
/// same id before and after means "the same pane survived the rebuild".
/// Null means "nothing there": no leaf held the role before, or none
/// holds it after. <see cref="OriginEdge"/> is the divider or dock side
/// the change came from when one applies, <see cref="PaneEdge.None"/>
/// otherwise.
/// </summary>
/// <param name="Kind">The structural operation that is about to run.</param>
/// <param name="BeforeLeafId">The leaf the transition starts from, if any.</param>
/// <param name="AfterLeafId">The leaf the transition lands on, if any.</param>
/// <param name="OriginEdge">The divider or dock edge the change is anchored to, if any.</param>
internal sealed record PaneTreeChange(
    PaneTreeChangeKind Kind,
    int? BeforeLeafId = null,
    int? AfterLeafId = null,
    PaneEdge OriginEdge = PaneEdge.None);

/// <summary>
/// One focus move between leaves. Null means the edge of the window:
/// focus arriving from nothing (first activation) or leaving to nothing
/// (last deactivation). Ids are the same opaque reporter-assigned
/// integers <see cref="PaneTreeChange"/> carries.
/// </summary>
/// <param name="FromLeafId">The leaf losing focus, if any.</param>
/// <param name="ToLeafId">The leaf gaining focus, if any.</param>
internal sealed record FocusTransfer(int? FromLeafId, int? ToLeafId);

/// <summary>
/// Neutral in-process notification surface for pane/tab/layout lifecycle
/// transitions. No-op unless a coordinator is registered (default: none)
/// -- call sites consult <see cref="PaneMotion.Active"/> (or read
/// <see cref="PaneMotion.Current"/>) and otherwise do nothing, so an
/// unobserved shell pays a null check. The single observer registers
/// through <see cref="PaneMotion.Register"/>.
/// </summary>
internal interface IPaneMotionCoordinator
{
    /// <summary>
    /// Resolves how much motion the named surface class may run, before
    /// the caller starts any.
    /// </summary>
    MotionPolicyLevel ResolvePolicy(MotionSurfaceClass surface);

    /// <summary>
    /// A pane-tree rebuild is about to start: the pre-rebuild snapshot
    /// point, taken while the outgoing tree is still intact.
    /// </summary>
    void OnPaneTreeChanging(PaneTreeChange change);

    /// <summary>
    /// A pane-tree rebuild committed: the post-rebuild commit point,
    /// taken once the incoming tree is live.
    /// </summary>
    void OnPaneTreeChanged(PaneTreeChange change);

    /// <summary>
    /// Focus moved between leaves (or arrived at / left the window edge).
    /// </summary>
    void OnFocusTransfer(FocusTransfer transfer);

    /// <summary>
    /// An overlay surface is about to open.
    /// </summary>
    void OnOverlayOpening(OverlayKind kind);

    /// <summary>
    /// An overlay surface finished dismissing.
    /// </summary>
    void OnOverlayDismissed(OverlayKind kind);
}
