using System;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The event-shape state machine, driven rather than read: the whole of
/// wintty#1158's classification, with the clock a test turns by hand.
/// </summary>
/// <remarks>
/// <para>Every sequence an editor, a sync client or a network provider can
/// produce is a list of kinds here, and every assertion runs the real
/// machine. The wiring that feeds it lives in
/// <c>ConfigWatcherEventShapeTests</c>, through the real watcher.</para>
///
/// <para>Two properties carry the design and each is pinned on its own, so
/// a mutation of either kills a different test: the RESOLUTION (an
/// appearance ends a pending at any delay, whatever the clock says) and
/// the BACKSTOP (only a pending with no reappearance past it concludes,
/// and it never re-arms on a repeat). The clock never decides that
/// something is a save; it only decides when the user is told about a
/// deletion.</para>
/// </remarks>
public class ConfigFilePresenceTests
{
    private sealed class Machine
    {
        private TimeSpan _now = TimeSpan.FromHours(1);

        public ConfigFilePresence Presence { get; private set; } = null!;
        public List<ConfigFilePresenceChange> Moves { get; } = new();

        public void Advance(TimeSpan by) => _now += by;

        public static Machine Create(TimeSpan? backstop = null)
        {
            var m = new Machine();
            m.Presence = backstop is { } bs
                ? new ConfigFilePresence(bs, m.Clock)
                : new ConfigFilePresence(now: m.Clock);
            m.Presence.StateChanged += m.Moves.Add;
            return m;
        }

        private TimeSpan Clock() => _now;

        private Machine() { }
    }

    [Fact]
    public void A_fresh_machine_is_present_and_reports_absence()
    {
        var m = Machine.Create();

        Assert.Equal(ConfigFilePresenceState.Present, m.Presence.State);
        Assert.True(m.Presence.AbsenceIsInstruction);
        Assert.Equal(ConfigFilePresence.DefaultBackstop, m.Presence.UntilBackstop);
        Assert.Empty(m.Moves);
    }

    [Fact]
    public void A_delete_opens_a_pending_and_nothing_else_happens_yet()
    {
        var m = Machine.Create();

        var change = m.Presence.Observe(ConfigFileEventKind.Deleted);

        Assert.Equal(ConfigFilePresenceState.PendingDelete, change.State);
        Assert.True(change.StateChanged);
        Assert.Equal(ConfigFilePresence.DefaultBackstop, m.Presence.UntilBackstop);
        Assert.False(m.Presence.AbsenceIsInstruction);
        Assert.Equal(1, m.Moves.Count);
    }

    /// <summary>THE RESOLUTION. The save's completing half, however late:
    /// a gap that outlives the old 900ms floor several times over is an
    /// ordinary afternoon on a network or synced profile, and the machine
    /// reads it exactly as it reads a 22ms one.</summary>
    [Theory]
    [InlineData(22)]
    [InlineData(900)]
    [InlineData(5000)]
    [InlineData(3_600_000)]
    public void An_appearance_resolves_a_pending_at_any_delay(int gapMs)
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Advance(TimeSpan.FromMilliseconds(gapMs));

        var change = m.Presence.Observe(ConfigFileEventKind.Appeared);

        Assert.Equal(ConfigFilePresenceState.Present, change.State);
        Assert.True(change.StateChanged);
        Assert.True(m.Presence.AbsenceIsInstruction);
    }

    /// <summary>THE BACKSTOP. A pending with no reappearance concludes
    /// exactly one backstop after its first delete, and only then.</summary>
    [Fact]
    public void A_pending_concludes_at_its_backstop_and_not_before()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);

        m.Advance(ConfigFilePresence.DefaultBackstop - TimeSpan.FromMilliseconds(1));
        var early = m.Presence.Poll();
        Assert.False(early.StateChanged);
        Assert.Equal(ConfigFilePresenceState.PendingDelete, m.Presence.State);

        m.Advance(TimeSpan.FromMilliseconds(1));
        var due = m.Presence.Poll();
        Assert.Equal(ConfigFilePresenceState.Gone, due.State);
        Assert.True(due.StateChanged);
    }

    [Fact]
    public void A_poll_with_nothing_open_concludes_nothing()
    {
        var m = Machine.Create();

        m.Advance(ConfigFilePresence.DefaultBackstop * 10);
        Assert.False(m.Presence.Poll().StateChanged);

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Presence.Observe(ConfigFileEventKind.Appeared);
        m.Advance(ConfigFilePresence.DefaultBackstop * 10);
        Assert.False(m.Presence.Poll().StateChanged);

        // And a concluded deletion stays concluded: a second poll does not
        // re-raise it.
        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Advance(ConfigFilePresence.DefaultBackstop);
        Assert.True(m.Presence.Poll().StateChanged);
        Assert.False(m.Presence.Poll().StateChanged);
    }

    /// <summary>A Changed report never resolves a pending. A file cannot
    /// change without having been recreated, but a pre-delete write's
    /// buffered report can arrive after the delete, and believing it would
    /// cancel a real deletion's notice.</summary>
    [Fact]
    public void A_changed_event_never_resolves_a_pending()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        var change = m.Presence.Observe(ConfigFileEventKind.Changed);

        Assert.False(change.StateChanged);
        Assert.Equal(ConfigFilePresenceState.PendingDelete, m.Presence.State);

        // It does not conclude one either: that is the poll's job.
        m.Advance(ConfigFilePresence.DefaultBackstop);
        Assert.Equal(ConfigFilePresenceState.Gone, m.Presence.Poll().State);
    }

    [Fact]
    public void Live_evidence_resolves_a_pending()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        var change = m.Presence.SeePresent();

        Assert.Equal(ConfigFilePresenceState.Present, change.State);
        Assert.True(m.Presence.AbsenceIsInstruction);
    }

    /// <summary>Last event wins. A save that lands after a concluded
    /// deletion reopens the session's claim, and the notice's premise is
    /// over.</summary>
    [Fact]
    public void An_appearance_reopens_a_concluded_deletion()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Advance(ConfigFilePresence.DefaultBackstop);
        m.Presence.Poll();
        Assert.Equal(ConfigFilePresenceState.Gone, m.Presence.State);

        var change = m.Presence.SeePresent();
        Assert.Equal(ConfigFilePresenceState.Present, change.State);
        Assert.True(m.Presence.AbsenceIsInstruction);

        // And a later delete is a fresh episode with a fresh backstop.
        m.Advance(TimeSpan.FromMinutes(5));
        m.Presence.Observe(ConfigFileEventKind.Deleted);
        Assert.Equal(ConfigFilePresence.DefaultBackstop, m.Presence.UntilBackstop);
    }

    /// <summary>A provider that repeats its delete report must not be able
    /// to postpone the notice: the clock belongs to the episode's first
    /// report, and a second one inside the pending is the same news.</summary>
    [Fact]
    public void Repeated_deletes_do_not_re_arm_the_backstop()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Advance(ConfigFilePresence.DefaultBackstop - TimeSpan.FromSeconds(4));

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        Assert.Equal(TimeSpan.FromSeconds(4), m.Presence.UntilBackstop);

        m.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(ConfigFilePresenceState.Gone, m.Presence.Poll().State);
    }

    /// <summary>A clock stepped back inside a pending cannot conclude early:
    /// the window restarts from the stepped time, the same rule the vanish
    /// confirmation holds to.</summary>
    [Fact]
    public void A_clock_stepped_back_restarts_the_window()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Advance(TimeSpan.FromSeconds(9));
        m.Advance(TimeSpan.FromDays(-60));

        // The stepped-back clock reads as a fresh window, never as one
        // already expired.
        Assert.Equal(ConfigFilePresence.DefaultBackstop, m.Presence.UntilBackstop);
        Assert.False(m.Presence.Poll().StateChanged);

        m.Advance(ConfigFilePresence.DefaultBackstop);
        Assert.Equal(ConfigFilePresenceState.Gone, m.Presence.Poll().State);
    }

    [Fact]
    public void Until_backstop_is_never_more_than_one_backstop()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        foreach (var step in new[] { -1, -300, -86_400_000, 5, 200 })
        {
            m.Advance(TimeSpan.FromMilliseconds(step));
            Assert.InRange(m.Presence.UntilBackstop, TimeSpan.Zero, ConfigFilePresence.DefaultBackstop);
        }
    }

    /// <summary>The state moves exactly once per move, so the host's notice
    /// wiring cannot be driven twice by one transition.</summary>
    [Fact]
    public void Each_move_raises_its_event_once()
    {
        var m = Machine.Create();

        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Presence.Observe(ConfigFileEventKind.Deleted);
        m.Presence.Observe(ConfigFileEventKind.Appeared);
        m.Presence.SeePresent();

        Assert.Equal(2, m.Moves.Count);
    }

    /// <summary>The backstop is set generous on purpose, because the branch
    /// it gates is only the notice: being slow there is safe, and being
    /// eager is what reset sessions to defaults. It must at least outlive
    /// the floor it replaces.</summary>
    [Fact]
    public void The_backstop_is_generous_against_the_measurements()
    {
        Assert.True(
            ConfigFilePresence.DefaultBackstop > ConfigVanishConfirmer.DefaultFloor,
            $"the backstop ({ConfigFilePresence.DefaultBackstop.TotalSeconds}s) must outlive " +
            $"the deletion dwell it replaces ({ConfigVanishConfirmer.DefaultFloor.TotalMilliseconds}ms), " +
            "or a slow save the machine would have resolved is concluded anyway");
    }

    /// <summary>A backstop of zero or less concludes on the first poll,
    /// which is the dwell's defect in a new coat, so it is not a
    /// configuration this can be put into.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_backstop_that_would_conclude_at_once_is_refused(int ms)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConfigFilePresence(TimeSpan.FromMilliseconds(ms)));
    }
}
