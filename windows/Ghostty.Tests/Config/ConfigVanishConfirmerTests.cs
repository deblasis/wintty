using System;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The vanish confirmation, driven rather than read.
/// </summary>
/// <remarks>
/// <para>These exist because the source-shape tests over <c>ConfigService</c>
/// could not fail: nothing executes that file, so a budget of zero and an
/// increment moved off its guard both passed the whole suite while restoring
/// the #1146 defect exactly. Every assertion here runs the thing.</para>
///
/// <para>Two behaviours carry the design and each is pinned on its own, so a
/// mutation of either kills a different test: the FLOOR and the RESET. An
/// earlier revision also carried an observation count, which is gone. The
/// floor is measured from the first observation, so it already required a
/// second, and the count could never bind.</para>
/// </remarks>
public class ConfigVanishConfirmerTests
{
    private static Func<bool> Armed => () => true;
    private static Func<bool> Dropped => () => false;

    private sealed class Clock
    {
        private TimeSpan _now = TimeSpan.FromHours(1);
        public Func<TimeSpan> Now => () => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// THE FLOOR, first half. One observation never proves a deletion,
    /// because the floor is measured from that observation. This is what
    /// makes a second one necessary, and it carries the weight a separate
    /// count used to appear to.
    /// </summary>
    [Fact]
    public void One_observation_never_proves_a_deletion()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
    }

    /// <summary>
    /// THE FLOOR, second half. A burst of observations inside one absence
    /// window is not a deletion: an ordinary save leaves the path absent for
    /// up to 22ms measured, and every reload landing in that window reports
    /// the same thing.
    /// </summary>
    [Fact]
    public void A_burst_inside_one_absence_window_is_not_a_deletion()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
            clock.Advance(ConfigVanishConfirmer.WidestMeasuredSaveWindow);
        }
    }

    /// <summary>And past the floor it is believed.</summary>
    [Fact]
    public void An_observation_past_the_floor_proves_a_deletion()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
        clock.Advance(ConfigVanishConfirmer.DefaultFloor);

        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Armed));
    }

    /// <summary>
    /// The floor is set as a multiple of the widest window measured across
    /// save shapes, not as a round number, so lowering it toward that
    /// measurement is caught here rather than in review.
    /// </summary>
    [Fact]
    public void The_floor_clears_the_widest_measured_save_window_by_a_wide_margin()
    {
        var margin = ConfigVanishConfirmer.DefaultFloor
            / ConfigVanishConfirmer.WidestMeasuredSaveWindow;

        Assert.True(
            margin >= 20,
            $"the floor is {margin:F1}x the widest measured save window " +
            "(22ms, rename-away-then-create); it was set at about 41x, and a " +
            "margin this small no longer excludes an ordinary save");
    }

    /// <summary>
    /// A floor of zero or less accepts the first report, which is the defect
    /// itself, so it is not a configuration this can be put into.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_floor_that_would_believe_the_first_report_is_refused(int ms)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConfigVanishConfirmer(TimeSpan.FromMilliseconds(ms)));
    }

    /// <summary>
    /// THE RESET. A load that found a file ends the stretch, so a later
    /// absence starts from nothing rather than inheriting elapsed time.
    /// </summary>
    /// <remarks>
    /// Without this a stretch outlives the question that opened it and the
    /// next ordinary save is believed on its first observation, because the
    /// clock started minutes ago. It is also the whole defence against
    /// correlated sampling: for two observations to both count, no load may
    /// have found the file between them.
    /// </remarks>
    [Fact]
    public void A_load_that_found_the_file_ends_the_stretch()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
        Assert.True(confirmer.Observing);

        confirmer.Reset();
        Assert.False(confirmer.Observing);

        // Long past the floor measured from the ORIGINAL first observation.
        clock.Advance(ConfigVanishConfirmer.DefaultFloor * 10);

        // Still not believed: this is the first observation of a new stretch.
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
    }

    /// <summary>
    /// A dropped ask holds nothing up. The observation already happened, so
    /// the stretch advances on wall time whether or not another look could
    /// be scheduled.
    /// </summary>
    /// <remarks>
    /// This is the default configuration and not an edge case:
    /// <c>auto-reload-config</c> is off by default, so there is no watcher
    /// to ask and the ask fails every time. Gating on it made a deletion
    /// unprovable for every user who never opted in.
    /// </remarks>
    [Fact]
    public void A_deletion_is_provable_with_no_watcher_to_ask()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        clock.Advance(ConfigVanishConfirmer.DefaultFloor);

        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Dropped));
    }

    /// <summary>
    /// The ask is put while the answer is open and not once it is settled,
    /// so a confirmed deletion does not keep scheduling looks.
    /// </summary>
    [Fact]
    public void The_ask_stops_once_the_answer_is_settled()
    {
        var asks = 0;
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        confirmer.Observe(1, () => { asks++; return true; });
        Assert.Equal(1, asks);

        clock.Advance(ConfigVanishConfirmer.DefaultFloor);
        Assert.Equal(
            ConfigVanishAction.Accept,
            confirmer.Observe(1, () => { asks++; return true; }));
        Assert.Equal(1, asks);
    }

    /// <summary>
    /// A session claiming no config file has nothing to lose and nothing to
    /// confirm, so a report neither opens a stretch nor concludes one.
    /// </summary>
    [Fact]
    public void A_session_running_on_no_config_file_ignores_the_report()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Ignore, confirmer.Observe(0, Armed));
        Assert.False(confirmer.Observing);

        clock.Advance(ConfigVanishConfirmer.DefaultFloor * 10);
        Assert.Equal(ConfigVanishAction.Ignore, confirmer.Observe(0, Armed));
    }

    /// <summary>
    /// And it asks for nothing. With no watcher an ask becomes a reload the
    /// host schedules for itself, and that reload finds the same absence:
    /// a session with nothing to lose, after an accepted deletion or under
    /// --no-config, that asked here would reload every floor for the life
    /// of the process.
    /// </summary>
    [Fact]
    public void A_session_running_on_no_config_file_asks_for_nothing()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);
        var asks = 0;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(ConfigVanishAction.Ignore,
                confirmer.Observe(0, () => { asks++; return true; }));
            clock.Advance(ConfigVanishConfirmer.DefaultFloor);
        }

        Assert.Equal(0, asks);
    }

    /// <summary>
    /// The time left until the floor is what a host with no watcher waits
    /// before looking again, so it has to name the first moment an
    /// observation can accept: measured from the stretch's first
    /// observation, not from the latest one, and zero once passed.
    /// </summary>
    [Fact]
    public void Until_floor_names_the_first_moment_an_observation_can_accept()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);
        var step = TimeSpan.FromMilliseconds(300);

        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, confirmer.UntilFloor);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, confirmer.UntilFloor);

        clock.Advance(step);
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor - step, confirmer.UntilFloor);

        // Waiting exactly that long is enough, and not a tick less.
        clock.Advance(confirmer.UntilFloor - TimeSpan.FromTicks(1));
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(TimeSpan.Zero, confirmer.UntilFloor);
        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Dropped));

        clock.Advance(step);
        Assert.Equal(TimeSpan.Zero, confirmer.UntilFloor);
    }

    /// <summary>
    /// A clock that steps BACK inside a stretch can neither stretch the wait
    /// nor accept early. The time left is never more than one floor, and the
    /// stretch restarts from the stepped time, so the first observation a
    /// floor after that concludes.
    /// </summary>
    /// <remarks>
    /// On the wall clock this was a real defect: 60 days back made the time
    /// left 60 days and 900ms, which a host with no watcher scheduled its
    /// one look for, and past about 49.7 days the timer threw inside the
    /// reload. The default clock is monotonic now, and cannot step; this
    /// holds the confirmer to it for any clock it is handed.
    /// </remarks>
    [Fact]
    public void A_clock_stepped_back_never_waits_past_one_floor_nor_accepts_early()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        clock.Advance(TimeSpan.FromMilliseconds(500));

        clock.Advance(TimeSpan.FromDays(-60));
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, confirmer.UntilFloor);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, confirmer.UntilFloor);

        clock.Advance(ConfigVanishConfirmer.DefaultFloor - TimeSpan.FromTicks(1));
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Dropped));
    }

    /// <summary>
    /// The time left is never more than the floor, whatever the clock does
    /// between two reads of it: it is a delay a host hands to a timer.
    /// </summary>
    [Fact]
    public void Until_floor_is_never_more_than_the_floor()
    {
        var clock = new Clock();
        var confirmer = new ConfigVanishConfirmer(now: clock.Now);

        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        foreach (var step in new[] { -1, -300, -86_400_000, 5, 200 })
        {
            clock.Advance(TimeSpan.FromMilliseconds(step));
            Assert.InRange(confirmer.UntilFloor, TimeSpan.Zero, ConfigVanishConfirmer.DefaultFloor);
        }
    }
}
