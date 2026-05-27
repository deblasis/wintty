using Xunit;

namespace Ghostty.Tests.Windows.Search;

public class SearchBarSmokeTests
{
    // Manual smoke specs for the in-pane scrollback search bar.
    //
    // SearchState behaviour is covered exhaustively by
    // SearchStateTests in Ghostty.Tests (pure-logic xUnit, no host).
    // The pieces these specs would exercise -- keyboard accelerator
    // routing into OpenSearch(), Visibility-toggle, debounce timer
    // ticks against the real DispatcherQueue, and the libghostty
    // round-trip for needle/total/selected updates -- all require a
    // real MainWindow on a dispatcher with a live libghostty surface.
    // That's the same architectural blocker that keeps
    // NewTabSplitButtonSmokeTests and ProfileChordSmokeTests
    // un-automated.
    //
    // To validate by hand once the binary lands:
    //
    // 1. Open wintty, run `seq 1 5000` to fill the scrollback.
    // 2. Press Ctrl+Shift+F. The search bar appears at the top-right,
    //    the needle textbox has focus, and any prior text is
    //    pre-selected.
    // 3. Type "500". After ~80ms the counter shows "1 of 1" and the
    //    match in the scrollback highlights.
    // 4. Backspace then type "1". The counter updates to e.g.
    //    "1 of 271" (one per line containing "1" in the range).
    // 5. Press Enter. The "selected" index advances by one and the
    //    counter shows "2 of 271".
    // 6. Press Shift+Enter. The index moves back to 1.
    // 7. Press Esc. The search bar collapses; focus returns to the
    //    terminal surface so typing reaches the shell.
    // 8. Press Ctrl+Shift+F again to confirm reopen works and
    //    the counter resets to "" until a new needle is typed.
    [Fact(Skip = "Manual smoke; SearchState pure-logic coverage lives in Ghostty.Tests.Search.SearchStateTests.")]
    public void CtrlShiftF_OpensSearchBar_DebouncesNeedle_StepsMatches_EscRestoresFocus()
    {
    }

    // Smoke spec for the libghostty -> apprt callback round trip.
    //
    // libghostty fires GHOSTTY_ACTION_SEARCH_TOTAL and
    // GHOSTTY_ACTION_SEARCH_SELECTED as the user navigates; the
    // counter updates depend on GhosttyHost.OnAction routing those
    // payloads to TerminalControl.OnSearchTotalChanged /
    // OnSearchSelectedChanged on the UI thread. The ordinals
    // (61 / 62) and struct layouts are pinned by
    // GhosttyActionsLayoutTests; this spec exists to remind us that
    // the live wiring needs a real surface to verify end-to-end.
    [Fact(Skip = "Manual smoke; ordinals/struct sizes covered by GhosttyActionsLayoutTests.")]
    public void SearchTotalAndSelected_FromLibghostty_FlowToCounterText()
    {
    }
}
