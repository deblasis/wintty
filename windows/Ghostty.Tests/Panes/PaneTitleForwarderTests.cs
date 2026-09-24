using System;
using System.Collections.Generic;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

/// <summary>
/// The pane host's title rules, driven with fake terminals: every terminal
/// is heard for its whole life, only the active one names the tab, and a
/// focus change hands the tab the newly active pane's title, null included.
/// The pane host itself only feeds this class (see TabTitleWiringTests for
/// the one check that it does).
/// </summary>
public class PaneTitleForwarderTests
{
    private sealed class FakeTerminal
    {
        public string? CurrentTitle { get; private set; }
        public event EventHandler<string>? TitleChanged;
        public int Subscribers => TitleChanged?.GetInvocationList().Length ?? 0;

        public void SetTitle(string title)
        {
            CurrentTitle = title;
            TitleChanged?.Invoke(this, title);
        }
    }

    private sealed class Host
    {
        public FakeTerminal Active;
        public readonly List<string?> Emitted = new();
        public readonly PaneTitleForwarder<FakeTerminal> Forwarder;

        public Host(FakeTerminal first)
        {
            Active = first;
            Forwarder = new PaneTitleForwarder<FakeTerminal>(
                activeTerminal: () => Active,
                currentTitle: t => t.CurrentTitle,
                subscribe: (t, h) => t.TitleChanged += h,
                unsubscribe: (t, h) => t.TitleChanged -= h,
                emit: Emitted.Add);
            Forwarder.Track(first);
        }

        public void Focus(FakeTerminal t)
        {
            Active = t;
            Forwarder.ActiveChanged();
        }
    }

    [Fact]
    public void TheActivePanesTitle_ReachesTheTab()
    {
        var a = new FakeTerminal();
        var host = new Host(a);

        a.SetTitle("vim notes.md");

        Assert.Equal(new[] { "vim notes.md" }, host.Emitted);
    }

    [Fact]
    public void AnUnfocusedPanesTitle_IsDropped()
    {
        var a = new FakeTerminal();
        var b = new FakeTerminal();
        var host = new Host(a);
        host.Forwarder.Track(b);

        b.SetTitle("htop");

        Assert.Empty(host.Emitted);
    }

    [Fact]
    public void AFocusChange_HandsTheTabTheNewPanesTitle_AndLaterTitlesFromIt()
    {
        var a = new FakeTerminal();
        var b = new FakeTerminal();
        var host = new Host(a);
        host.Forwarder.Track(b);
        a.SetTitle("A");
        b.SetTitle("B");

        host.Focus(b);
        a.SetTitle("A2");
        b.SetTitle("B2");

        Assert.Equal(new[] { "A", "B", "B2" }, host.Emitted);
    }

    /// <summary>
    /// The founder's ruling, the Windows Terminal behaviour: focusing a pane
    /// that has not reported a title hands the tab null, so the tab shows
    /// that pane's fallback. Keeping the previous pane's title would name a
    /// pane that is no longer focused.
    /// </summary>
    [Fact]
    public void FocusingAnUntitledPane_EmitsNull_NotThePreviousTitle()
    {
        var a = new FakeTerminal();
        var fresh = new FakeTerminal();
        var host = new Host(a);
        host.Forwarder.Track(fresh);
        a.SetTitle("ssh prod");

        host.Focus(fresh);

        Assert.Equal(new string?[] { "ssh prod", null }, host.Emitted);
    }

    [Fact]
    public void Track_IsIdempotent_AndUntrackTakesTheHandlerOff()
    {
        var a = new FakeTerminal();
        var host = new Host(a);
        host.Forwarder.Track(a);
        Assert.Equal(1, a.Subscribers);

        host.Forwarder.Untrack(a);
        a.SetTitle("gone");

        Assert.Equal(0, a.Subscribers);
        Assert.Equal(0, host.Forwarder.TrackedCount);
        Assert.Empty(host.Emitted);
    }

    [Fact]
    public void Stop_UnsubscribesEveryTerminal_AndEmitsNothingMore()
    {
        var a = new FakeTerminal();
        var b = new FakeTerminal();
        var host = new Host(a);
        host.Forwarder.Track(b);

        host.Forwarder.Stop();
        a.SetTitle("late");
        host.Forwarder.ActiveChanged();
        host.Forwarder.Track(b);

        Assert.Equal(0, a.Subscribers);
        Assert.Equal(0, b.Subscribers);
        Assert.Empty(host.Emitted);
    }
}
