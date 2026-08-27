using System;
using System.Collections.Generic;
using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Routing a process-wide request to one window.
///
/// The real callers pass MainWindow, which this project cannot reference, so
/// the window here is a record with the two flags the shell's predicate reads.
/// That is the whole reason the decision lives in Ghostty.Core: inline in App
/// it would only ever be exercised by a person with two windows open.
/// </summary>
public sealed class ActiveWindowTargetTests
{
    private sealed record FakeWindow(string Name, bool Quake = false, bool Closing = false);

    // The shell's predicate, verbatim in shape. The closing half is the one
    // that fires in the shell; the quake half is a backstop, since the shell
    // keeps the quake window out of the registry and out of last-activated
    // both. This helper lives in Core and can see neither, so it has to
    // answer for a quake window it is handed anyway.
    private static bool Eligible(FakeWindow w) => !w.Quake && !w.Closing;

    private static FakeWindow? Choose(FakeWindow? lastActivated, params FakeWindow[] all) =>
        ActiveWindowTarget.Choose(lastActivated, all, Eligible);

    [Fact]
    public void NoWindowsAtAllChoosesNothing()
    {
        Assert.Null(Choose(null));
    }

    [Fact]
    public void TheOnlyEligibleWindowIsChosen()
    {
        var only = new FakeWindow("only");
        Assert.Same(only, Choose(null, only));
    }

    [Fact]
    public void TheLastActivatedWindowWinsOverListOrder()
    {
        var first = new FakeWindow("first");
        var last = new FakeWindow("last");

        // Enumeration order would hand back `first`; the point of tracking
        // activation is that it does not.
        Assert.Same(last, Choose(last, first, last));
    }

    [Fact]
    public void AClosingLastActivatedWindowGivesWayToTheNextEligibleOne()
    {
        var closing = new FakeWindow("closing", Closing: true);
        var open = new FakeWindow("open");

        Assert.Same(open, Choose(closing, closing, open));
    }

    [Fact]
    public void AQuakeOnlySessionChoosesNothing()
    {
        // "Some window exists" is not the same question as "some window can
        // take this". The shell excludes the quake window upstream, so this
        // is the backstop being exercised rather than a state the shell
        // produces.
        var quake = new FakeWindow("quake", Quake: true);

        Assert.Null(Choose(quake, quake));
        Assert.Null(Choose(null, quake));
    }

    [Fact]
    public void AClosingLastActivatedWindowWithNoEligibleRestChoosesNothing()
    {
        // The failure this guards: falling back to the last-activated window
        // anyway because it is the only one there is.
        var closing = new FakeWindow("closing", Closing: true);
        var quake = new FakeWindow("quake", Quake: true);

        Assert.Null(Choose(closing, closing, quake));
    }

    [Fact]
    public void ALastActivatedWindowMissingFromTheListIsStillChosenWhenEligible()
    {
        // The two arguments come from different places in App (a field and a
        // dictionary's values), and there is no moment where they are read
        // atomically. Preferring the field when it is eligible is the
        // documented rule, not an accident of it appearing in the list.
        var detached = new FakeWindow("detached");
        var other = new FakeWindow("other");

        Assert.Same(detached, Choose(detached, other));
    }

    [Fact]
    public void TheEligiblePredicateIsAskedAboutTheLastActivatedWindowOnlyOnce()
    {
        // The fallback scan skips the last-activated window rather than
        // re-testing it. A predicate that is expensive or that changes its
        // answer would otherwise be able to return a window it just rejected.
        var last = new FakeWindow("last", Closing: true);
        var asked = new List<string>();

        var chosen = ActiveWindowTarget.Choose<FakeWindow>(
            last,
            new[] { last },
            w => { asked.Add(w.Name); return Eligible(w); });

        Assert.Null(chosen);
        Assert.Equal(new[] { "last" }, asked);
    }
}
