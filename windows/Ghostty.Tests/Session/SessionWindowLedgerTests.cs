using System.Collections.Generic;
using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

/// <summary>
/// <see cref="SessionWindowLedger{TKey}"/>: what the clean-shutdown write
/// reads instead of the file.
///
/// The defect: at app shutdown no window is live, so re-reading the file
/// and flipping <see cref="SessionState.CleanShutdown"/> can only mark
/// whatever the last debounced persist saw. A rename, a pin, a group edit
/// or a cd made inside that window was saved stale, and a multi-window
/// quit lost every window that closed before the debounce fired. The
/// ledger keeps the captures, so the closing window's own state is what
/// lands.
/// </summary>
public class SessionWindowLedgerTests
{
    private static WindowSession Window(string title)
        => new() { Tabs = { new TabSession { UserTitle = title, Tree = new LeafDto { ProfileId = "pwsh" } } } };

    [Fact]
    public void The_first_close_of_a_quit_appends_the_window()
    {
        // A window that has never been persisted: the cascade case, where
        // dropping it would restore one window instead of three.
        var ledger = new SessionWindowLedger<string>();
        ledger.Capture("a", Window("one"));

        Assert.Equal(1, ledger.Count);
        Assert.Equal("one", Assert.Single(ledger.Sessions).Tabs[0].UserTitle);
    }

    [Fact]
    public void Re_capturing_a_window_replaces_it_in_place()
    {
        var ledger = new SessionWindowLedger<string>();
        ledger.Capture("a", Window("A"));
        ledger.Capture("b", Window("B"));

        // The rename in the last second before the X: same window, new
        // state, and no second copy of it in the set.
        ledger.Capture("a", Window("A renamed"));

        var windows = ledger.Sessions;
        Assert.Equal(2, windows.Count);
        Assert.Equal("A renamed", windows[0].Tabs[0].UserTitle);
        Assert.Equal("B", windows[1].Tabs[0].UserTitle);
    }

    [Fact]
    public void Every_closed_window_of_a_quit_is_kept()
    {
        // Three windows, none of which was ever persisted, closing in a
        // row. The clean write has to carry all three: the set is never
        // shrunk on close, because a single deliberate close is
        // indistinguishable from the first close of a quit.
        var ledger = new SessionWindowLedger<string>();
        ledger.Capture("a", Window("one"));
        ledger.Capture("b", Window("two"));
        ledger.Capture("c", Window("three"));

        Assert.Equal(3, ledger.Count);
    }

    [Fact]
    public void ReplaceAll_makes_the_ledger_the_live_set()
    {
        var ledger = new SessionWindowLedger<string>();
        ledger.Capture("a", Window("one"));
        ledger.Capture("b", Window("two"));

        // The debounced persist wrote {b}: a is gone from the live set, so
        // it is gone from the ledger too. A later close must not resurrect
        // a window the file has already moved past.
        ledger.ReplaceAll([("b", Window("two, later"))]);

        var windows = ledger.Sessions;
        Assert.Equal(1, windows.Count);
        Assert.Equal("two, later", windows[0].Tabs[0].UserTitle);
    }

    [Fact]
    public void Sessions_is_a_copy_the_caller_may_keep()
    {
        var ledger = new SessionWindowLedger<string>();
        ledger.Capture("a", Window("one"));

        var taken = ledger.Sessions;
        ledger.Capture("b", Window("two"));

        // A view over the ledger would grow under the state that is about
        // to be serialized, and the write is what restore reads.
        Assert.Single(taken);
    }

    [Fact]
    public void A_clean_state_carries_every_captured_window()
    {
        var ledger = new SessionWindowLedger<string>();
        ledger.Capture("a", Window("one"));
        ledger.Capture("b", Window("two"));
        ledger.Capture("a", Window("one renamed"));

        // The shape FinalizeCleanShutdown writes.
        var state = new SessionState { CleanShutdown = true };
        state.Windows.AddRange(ledger.Sessions);

        Assert.True(state.CleanShutdown);
        Assert.Equal(2, state.Windows.Count);
        Assert.Equal("one renamed", state.Windows[0].Tabs[0].UserTitle);
        Assert.Equal("two", state.Windows[1].Tabs[0].UserTitle);
    }
}