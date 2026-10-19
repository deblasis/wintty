using System;
using System.Collections.Generic;
using System.Threading;
using Ghostty.Core.Config;
using Ghostty.Core.Notifications;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The vanish protocol's no-watcher paths, end to end: load verdicts the
/// only evidence, every look driven by hand through the ask chain, and a
/// real <see cref="NotificationService"/> holding the banner a confirmed
/// deletion raises.
/// </summary>
/// <remarks>
/// <para>The service wires exactly this and nothing executes the service,
/// so these sequences lived only as source shape until the wiring moved
/// into Ghostty.Core. Every assertion here runs the real thing.</para>
///
/// <para>Since wintty#1158 this protocol owns the paths that never see a
/// file event: no watcher (the default, <c>auto-reload-config</c> off), a
/// lost event, a deleted directory. On the watcher's own path the
/// event-shape machine concludes first, and that wiring is driven in
/// <c>ConfigWatcherEventShapeTests</c>; what a concluded deletion CAUSES
/// is the same notice whichever way it was proven, so the onAccept here
/// calls the notice source the same way the service's
/// <c>OnVanishAccepted</c> does.</para>
/// </remarks>
public sealed class ConfigVanishProtocolTests : IDisposable
{
    private readonly NotificationService _notifications = new();

    // The session's recorded count, what RecordDefaultFiles owns in the
    // service: raised by an applied load. A concluded deletion does not
    // move it, because nothing is applied; it falls only when a reload
    // that the session asked for (the notice's load-defaults action) or a
    // shrink past the floor carries the gate.
    private int _count = 1;
    private int _accepted;
    private int _defaultsAsked;

    private TimeSpan _now = TimeSpan.FromHours(1);

    private ConfigFilePresence? _presence;
    private ConfigFileMissingNoticeSource? _source;

    public void Dispose()
    {
        _presence = null;
        _source = null;
    }

    private void AdvancePastFloor() => _now += ConfigVanishConfirmer.DefaultFloor;

    /// <summary>
    /// The wiring the service has, minus the shell: the protocol's accept
    /// lands in the notice source exactly as OnVanishAccepted lands there,
    /// with a machine it never hears from because nothing here raises a
    /// file event.
    /// </summary>
    private ConfigVanishProtocol Start(
        Func<bool>? ask = null,
        List<TimeSpan>? looks = null)
    {
        _presence = new ConfigFilePresence(now: () => _now);
        _source = new ConfigFileMissingNoticeSource(
            _presence,
            notifications: () => _notifications,
            scheduleBackstopPoll: _ => true,
            loadDefaults: () => Interlocked.Increment(ref _defaultsAsked));
        return new ConfigVanishProtocol(
            sessionDefaultFilesFound: () => _count,
            ask: ask ?? (() => false),
            onAccept: () =>
            {
                Interlocked.Increment(ref _accepted);
                _source.OnDeletionConfirmed();
            },
            now: () => _now,
            lookAgainAfter: looks is null
                ? null
                : delay => { looks.Add(delay); return true; });
    }

    /// <summary>
    /// A file that is there is never confirmed gone, however long the
    /// session runs and however often it is loaded.
    /// </summary>
    /// <remarks>
    /// This is the half of the verdict rule that no deletion sequence can
    /// see. Every one of those starts from a real absence, so swapping which
    /// verdict opens a stretch breaks them for a reason that almost any
    /// other broken rule also produces. Here the file never goes away, so a
    /// conclusion can only come from treating a load that FOUND it as
    /// evidence that it is gone.
    /// </remarks>
    [Fact]
    public void A_file_that_is_there_is_never_confirmed_gone()
    {
        var protocol = Start();

        Assert.False(protocol.Observed(ConfigFilesFound.Loaded));
        AdvancePastFloor();
        Assert.False(protocol.Observed(ConfigFilesFound.Loaded));
        AdvancePastFloor();
        Assert.False(protocol.Observed(ConfigFilesFound.Loaded));

        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
    }

    /// <summary>
    /// A deletion is provable with no watcher to ask. The ask buys a
    /// sooner look and nothing hangs on its answer: the observation has
    /// already happened, so whether another can be scheduled says nothing
    /// about it.
    /// </summary>
    /// <remarks>
    /// An earlier revision advanced only on asks that SCHEDULED, which
    /// wedged this in the believing-nothing direction whenever no watcher
    /// existed. That is the default configuration, since
    /// <c>auto-reload-config</c> is off: the ask always failed, nothing
    /// ever advanced, and the session declined every later reload for the
    /// life of the process. Issue #676 reached through its own fix, filed
    /// as wintty#1155. The test that shipped beside that revision asserted
    /// the lockout as intended behaviour, so the defect had coverage
    /// pointing the wrong way; this is the same sequence with the
    /// assertion the other way round.
    /// </remarks>
    [Fact]
    public void A_deletion_concludes_even_when_no_ask_can_be_scheduled()
    {
        var protocol = Start(ask: () => false);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));

        // Nothing was scheduled, so no delivery will revisit this. One
        // report proves nothing either way, so the count still stands and
        // no banner is up.
        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
        Assert.False(_source!.NoticeShowing);

        // The next observation arrives the way it does when there is no
        // watcher at all: some other reload, a theme change or a settings
        // edit, builds the config and finds no default file. Past the
        // floor, that is proof, and what it proves lands in the notice:
        // the settings stand, and the user is told.
        AdvancePastFloor();
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);
        Assert.Equal(1, _count);
        Assert.True(_source.NoticeShowing);
        Assert.Single(_notifications.Active);
    }

    /// <summary>
    /// With no watcher, the reload that finds the file gone is enough on its
    /// own. It cannot conclude, because the floor is measured from it, so the
    /// protocol schedules one look exactly as far out as the floor, and that
    /// look concludes.
    /// </summary>
    /// <remarks>
    /// Before this, nothing looked again. The question waited for the next
    /// reload somebody happened to cause, and the reload that opened it, a
    /// High Contrast toggle, was declined and lost: deleting the config file
    /// cost the user a second toggle, at least 900ms after the first
    /// (wintty#1155). What the look concludes into is now the notice, not an
    /// applied defaults config: the toggle still declines, and the notice is
    /// the way out of that, not a silent reset.
    /// </remarks>
    [Fact]
    public void With_no_watcher_one_reload_is_enough_because_the_protocol_looks_again()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        // The toggle's reload finds no file: an observation, not a proof.
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(0, _accepted);
        var look = Assert.Single(looks);
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, look);

        // The look fires when it was scheduled to, and its reload still
        // finds nothing. That is proof, with nobody touching anything.
        _now += look;
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);
        Assert.True(_source!.NoticeShowing);
        Assert.Single(looks);
    }

    /// <summary>
    /// The look is scheduled only when the watcher took no ask. With a
    /// watcher, its own deliveries already come every 300ms and a second
    /// source of reloads would only double the rebuilds.
    /// </summary>
    [Fact]
    public void The_look_again_is_not_taken_when_the_ask_was_taken()
    {
        var looks = new List<TimeSpan>();
        var protocol = new ConfigVanishProtocol(
            sessionDefaultFilesFound: () => _count,
            ask: () => true,
            onAccept: () => { },
            now: () => _now,
            lookAgainAfter: delay =>
            {
                looks.Add(delay);
                return true;
            });

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Empty(looks);
    }

    /// <summary>
    /// A file coming back ends the stretch, and the next absence schedules
    /// its look a whole floor from ITS first observation. A look timed from
    /// the old stretch would land before the new floor and conclude nothing,
    /// and with no watcher nothing would look after it.
    /// </summary>
    [Fact]
    public void A_fresh_stretch_times_its_look_from_its_own_first_observation()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += TimeSpan.FromMilliseconds(600);
        Assert.False(protocol.Observed(ConfigFilesFound.Loaded));

        _now += TimeSpan.FromMilliseconds(600);
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));

        Assert.Equal(2, looks.Count);
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, looks[1]);
        Assert.Equal(0, _accepted);
    }

    /// <summary>
    /// No look once the question is settled, and none once there is
    /// nothing left to lose. After an accepted deletion the question is on
    /// hold, and every later absence answers Hold without asking; a look
    /// scheduled from there would reload for the life of the process. The
    /// same holds from startup under --no-config, where the count is
    /// pinned at zero.
    /// </summary>
    [Fact]
    public void No_look_is_scheduled_once_the_question_is_settled_or_unowned()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += looks[0];
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));

        // Settled: the next absences are holds, and none of them asks.
        for (var i = 0; i < 3; i++)
        {
            _now += ConfigVanishConfirmer.DefaultFloor;
            Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        }
        Assert.Single(looks);

        // A session that never had one asks for nothing from the start.
        var none = new List<TimeSpan>();
        _count = 0;
        var fresh = Start(looks: none);
        Assert.False(fresh.Observed(ConfigFilesFound.Absent));
        Assert.Empty(none);
    }

    /// <summary>
    /// The hold ends only when the file is seen again. A load that found a
    /// file resets the question, so the next absence is a fresh one, with
    /// its own floor and its own conclusion.
    /// </summary>
    /// <remarks>
    /// This is the stale-stretch defence, on the only path that still has
    /// stretches: an accepted question whose file returns must not make
    /// the next save's first absence conclusive. It drove through the
    /// watcher's deliveries before the event machine existed; the verdicts
    /// are what carry it now.
    /// </remarks>
    [Fact]
    public void An_accepted_question_holds_until_the_file_returns()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += looks[0];
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);
        Assert.True(_source!.NoticeShowing);

        // Absences keep answering hold: once.
        AdvancePastFloor();
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);

        // The file returns: the verdict ends the question, and the reload
        // that carried it applies, which is what takes the banner down in
        // the service (the applied path calls OnReloadApplied). The verdict
        // alone dismisses nothing.
        Assert.False(protocol.Observed(ConfigFilesFound.Loaded));
        _source!.OnReloadApplied();
        Assert.False(_source.NoticeShowing);
        Assert.Empty(_notifications.Active);

        _now += TimeSpan.FromMilliseconds(100);
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, looks[1]);
        _now += looks[1];
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(2, _accepted);
    }

    /// <summary>
    /// A look that lands short of the floor, a timer tick early or an
    /// observation mid stretch, asks for exactly what is left rather than a
    /// whole floor again: the protocol hands the host the confirmer's
    /// remaining time, not a constant.
    /// </summary>
    [Fact]
    public void A_look_mid_stretch_asks_only_for_what_is_left()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += TimeSpan.FromMilliseconds(300);
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(TimeSpan.FromMilliseconds(600), looks[1]);

        // A look that fires a hair early concludes nothing and re-asks for
        // the hair.
        _now += looks[1] - TimeSpan.FromMilliseconds(1);
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(TimeSpan.FromMilliseconds(1), looks[2]);

        _now += looks[2];
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
    }

    /// <summary>
    /// A clock stepped back inside a stretch cannot push the look past one
    /// floor: the look the protocol asks for is at most a floor out, and it
    /// concludes. On the wall clock, a 60 day step asked for a look 60 days
    /// out, and a High Contrast toggle after deleting the config file waited
    /// that long with no watcher.
    /// </summary>
    [Fact]
    public void A_clock_stepped_back_asks_for_no_look_past_the_floor()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += TimeSpan.FromMilliseconds(400);
        _now -= TimeSpan.FromDays(60);
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));

        Assert.Equal(2, looks.Count);
        Assert.All(looks, look =>
            Assert.InRange(look, TimeSpan.Zero, ConfigVanishConfirmer.DefaultFloor));

        _now += looks[^1];
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);
    }

    /// <summary>
    /// A file that is there but will not open ends the stretch like one that
    /// reads. A lock, a sharing violation, an offline cloud placeholder or a
    /// scanner's hold all come back Unreadable, not Absent. Keeping the
    /// stretch open across one would let a later load in another save's gap
    /// accept on that single observation, its first already a floor old.
    /// </summary>
    [Fact]
    public void An_unreadable_verdict_ends_the_stretch()
    {
        var looks = new List<TimeSpan>();
        var protocol = Start(looks: looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.False(protocol.Observed(ConfigFilesFound.Unreadable));
        AdvancePastFloor();
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(0, _accepted);
    }

    /// <summary>
    /// A look that finds the question settled reaches nothing at all: no
    /// second accept, no second banner, no ask. The settle answers through
    /// the machine's or the protocol's own state, and the notice's dedup is
    /// the second belt.
    /// </summary>
    [Fact]
    public void An_accept_reaches_the_notice_once_per_episode()
    {
        var protocol = Start();

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        AdvancePastFloor();
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Single(_notifications.Active);

        // The user closes it. A later hold must not re-raise: the episode
        // is over.
        _notifications.Dismiss(_notifications.Active[0]);
        Assert.Empty(_notifications.Active);

        AdvancePastFloor();
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Empty(_notifications.Active);
        Assert.False(_source!.NoticeShowing);
    }
}
