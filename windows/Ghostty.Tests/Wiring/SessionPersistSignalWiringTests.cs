using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The two session-persistence facts that live in the WinUI half of the
/// shell, which cannot be loaded into a test host, so they are read off
/// the parsed source. Each one is a defect this fix closed:
///
///   - the tab signals. A rename, a pin, a group op and a cd reached the
///     file only by accident (whatever tab/layout change happened to
///     follow within the debounce). Track subscribed to five events, none
///     of them these.
///   - the recapture. FinalizeCleanShutdown re-read the file and flipped a
///     flag, so it marked whatever the last debounce saw -- never the
///     closing window.
///
/// The load-bearing logic those calls reach is tested where it can run:
/// <c>TabGroupSignalTests</c> for the group signal and
/// <c>SessionWindowLedgerTests</c> for the merge. What is left here is
/// "the shell calls them".
/// </summary>
public class SessionPersistSignalWiringTests
{
    private static ShellSource SessionManager() => ShellSource.Load("Session.SessionManager.cs");
    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// Track must hear every tab-level change the saved session reads.
    /// Asserted as an assignment per event, matched on the exact
    /// left-hand side: a handler attached to a different manager, or one
    /// of them dropped, is a silently dead subscription.
    /// </summary>
    [Fact]
    public void Track_subscribes_the_group_signal_and_the_per_tab_watch()
    {
        var track = SessionManager().Method("Track");

        Assert.True(
            Subscribes(track, "GroupsChanged"),
            "Track must subscribe the manager's GroupsChanged: the group ops "
                + "write no tab, so nothing else in this method hears a "
                + "rename, a collapse or a dissolve");

        // The per-tab signals are installed in one place so Track,
        // OnTabAdded and Untrack cannot drift apart on which of them a
        // tab carries.
        var watch = SessionManager().Method("WatchTab");
        Assert.True(Subscribes(watch, "CwdChanged"));
        Assert.True(Subscribes(watch, "PropertyChanged"));

        // And a tab added later gets the same watch, not half of one.
        Assert.Single(SessionManager().Method("OnTabAdded").Calls("WatchTab"));

        // Detach is the mirror, or the subscriptions outlive the tab. The
        // operator is checked as well as the target: a `-=` that had become
        // `+=` would leave the handler attached forever.
        Assert.True(Unsubscribes(SessionManager().Method("UnwatchTab"), "CwdChanged"));
        Assert.True(Unsubscribes(SessionManager().Method("UnwatchTab"), "PropertyChanged"));
    }

    /// <summary>
    /// A `+=` on <paramref name="target"/> under <paramref name="method"/>,
    /// matched on the tail of the target so <c>tm.GroupsChanged</c> and a
    /// bare <c>GroupsChanged</c> both count and neither a different
    /// receiver's field nor a comment does.
    /// </summary>
    private static bool Subscribes(MethodDeclarationSyntax method, string target)
        => Assignments(method).Any(a =>
            a.OperatorToken.ValueText == "+=" &&
            a.Left.ToString().EndsWith(target, System.StringComparison.Ordinal));

    private static bool Unsubscribes(MethodDeclarationSyntax method, string target)
        => Assignments(method).Any(a =>
            a.OperatorToken.ValueText == "-=" &&
            a.Left.ToString().EndsWith(target, System.StringComparison.Ordinal));

    private static System.Collections.Generic.List<AssignmentExpressionSyntax> Assignments(
        MethodDeclarationSyntax method)
        => method.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToList();

    /// <summary>
    /// The three tab properties whose change the file must see, named
    /// exactly: a name in the switch that no setter raises is a dead arm,
    /// and a name that raises for every prompt would make the debounce
    /// write on every keystroke.
    /// </summary>
    [Fact]
    public void The_tab_watch_reacts_to_the_three_saved_properties()
    {
        var cases = SessionManager().Method("OnTabPropertyChanged")
            .DescendantNodes().OfType<CaseSwitchLabelSyntax>()
            .Select(l => l.Value.ToString())
            .ToList();

        Assert.Contains("nameof(Ghostty.Core.Tabs.TabModel.UserOverrideTitle)", cases);
        Assert.Contains("nameof(Ghostty.Core.Tabs.TabModel.IsPinned)", cases);
        Assert.Contains("nameof(Ghostty.Core.Tabs.TabModel.Group)", cases);
        Assert.Equal(3, cases.Count);

        // Every arm has to end in the persist, not in a comment.
        var routed = SessionManager().Method("OnTabPropertyChanged")
            .DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "RequestPersist")
            .ToList();
        Assert.Single(routed);
    }

    /// <summary>
    /// The closing window is captured into the ledger, and the clean write
    /// reads the ledger. The old shape -- Load the file, flip the flag,
    /// Save -- is the defect itself, so it is asserted to come after the
    /// ledger rather than merely unused.
    /// </summary>
    [Fact]
    public void The_clean_write_comes_from_the_ledger_not_the_stale_file()
    {
        var finalize = SessionManager().Method("FinalizeCleanShutdown");

        // A property read, not a call: the ledger hands its windows over as
        // a list the state is built from.
        var fromLedger = finalize.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.ToString() == "_ledger.Sessions")
            .ToList();
        Assert.Single(fromLedger);

        // The on-disk fallback is allowed, but only after the ledger has
        // answered empty -- never as the first thing the method does.
        var load = finalize.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "_store.Load")
            .ToList();
        Assert.Single(load);
        Assert.True(
            fromLedger[0].SpanStart < load[0].SpanStart,
            "FinalizeCleanShutdown must consult the captured windows before "
                + "falling back to re-reading the file");

        // The closing window's own capture is the ledger's input.
        Assert.Single(
            SessionManager().Method("CaptureClosingWindow")
                .DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText().EndsWith("_ledger.Capture")));
    }

    /// <summary>
    /// The close-side capture has to happen while the panes are alive:
    /// it is one line above the walk that frees them. A move past the free
    /// reads a torn-down window, which is how the recapture could have
    /// been written and still lost the state.
    /// </summary>
    [Fact]
    public void The_window_hands_its_capture_over_before_the_panes_go()
    {
        var closed = MainWindow().Method("OnClosedAsync");

        var handOver = closed.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText().EndsWith("CaptureClosingWindow"));
        var dispose = closed.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.CalleeText() == "t.PaneHost.DisposeAllLeaves");

        Assert.True(
            handOver.SpanStart < dispose.SpanStart,
            "the closing window's session capture must be taken before its "
                + "leaves are disposed");
    }
}