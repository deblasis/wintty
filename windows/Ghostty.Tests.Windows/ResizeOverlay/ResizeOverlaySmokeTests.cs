using Xunit;

namespace Ghostty.Tests.Windows.ResizeOverlay;

public class ResizeOverlaySmokeTests
{
    // Manual smoke spec for the per-pane resize pill auto-hide.
    //
    // ResizeOverlayState behaviour (dedup, mode gating, SizeText) is
    // covered exhaustively by ResizeOverlayStateTests in Ghostty.Tests
    // (pure-logic xUnit, no host). The piece this spec guards lives in
    // ResizeOverlayControl and cannot be automated: it needs a real
    // DispatcherQueue (for the one-shot hide timer to tick) and a live
    // visual tree that reparents on split -- the same MainWindow +
    // dispatcher blocker that keeps SearchBarSmokeTests and the split
    // button / profile chord specs un-automated.
    //
    // REGRESSION (fixed): ResizeOverlayControl used to drop its hide
    // timer's Tick handler on Unloaded. WinUI 3 raises Unloaded on every
    // reparent (each split / rebuild), not only on teardown, so after the
    // first split every pre-existing pane's pill lost its auto-hide: the
    // restarted timer fired into a detached delegate and the pill stayed
    // stuck on screen. Only the newest, never-reparented pane still hid.
    // The fix keeps the timer + Tick handler wired for the control's whole
    // lifetime (the cycle is self-contained and GC-collected together).
    //
    // To validate by hand once the binary lands:
    //
    // 1. Open wintty (single pane). Drag the window border. A "cols x rows"
    //    pill appears and auto-hides ~750ms after the drag settles.
    // 2. Split into three or more panes (e.g. Ctrl+Shift+E then
    //    Ctrl+Shift+O) so the pre-existing panes reparent at least once.
    // 3. Drag the window border so ALL panes resize at once. Every pane's
    //    pill appears.
    // 4. Stop dragging and wait ~1 second WITHOUT touching the window.
    //    EVERY pane's pill must disappear -- not just one. Before the fix,
    //    one pill (the most recently created pane) hid and the rest stayed
    //    stuck visible.
    [Fact(Skip = "Manual smoke; hide timer needs a live DispatcherQueue and reparenting visual tree. Pure-logic coverage is in Ghostty.Tests.ResizeOverlay.ResizeOverlayStateTests.")]
    public void AllPanePills_AutoHideAfterResize_IncludingReparentedPanes()
    {
    }
}
