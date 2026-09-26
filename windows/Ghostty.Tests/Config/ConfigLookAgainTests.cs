using System;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The service's own "look again", driven: what a declined reload's ask
/// schedules when there is no watcher to take it, and the delay bounds.
/// </summary>
/// <remarks>
/// The sequences below mirror the decline branch of
/// <c>ConfigService.Reload</c> over the real <see cref="ConfigReloadGate"/>
/// rules, the way <c>ConfigVanishProtocolTests</c> mirrors its wiring:
/// nothing executes the service, and <c>ConfigWatcherWiringTests</c> pins
/// that its decline branch asks <see cref="ConfigLookAgain.Ask"/> and
/// nothing else.
/// </remarks>
public class ConfigLookAgainTests
{
    // The service's MaxDeclinedReloadRetries.
    private const int Max = 3;

    private readonly FakeTimer _timer = new();
    private TimeSpan _elapsed;

    private ConfigLookAgain NoWatcher() =>
        new(watcherAsk: () => false, _timer, onLook: () => { },
            maxDelay: ConfigVanishConfirmer.DefaultFloor);

    /// <summary>
    /// A session and its decline branch, as the service has them: the
    /// shrink stretch measured by the caller's clock, the floor the
    /// vanish confirmation's own.
    /// </summary>
    private sealed class Session
    {
        public int Count;
        public int Retries;
        public TimeSpan? Since;
        public int Reloads;
        public int Applied;

        public bool Reload(ConfigLookAgain look, ConfigFilesFound found, int count, TimeSpan elapsed)
        {
            Reloads++;

            // The stretch, as the service measures it: from the stretch's
            // own first observation, and a stretch nobody opened is young.
            var stretch = Since is { } since && elapsed >= since
                ? elapsed - since
                : TimeSpan.Zero;

            if (ConfigReloadGate.Decide(found, count, Count) == ConfigReloadDecision.Decline
                && !ConfigReloadGate.IsPersistentShrink(found, count, Count, stretch,
                    ConfigVanishConfirmer.DefaultFloor))
            {
                if (ConfigReloadGate.ShouldRetry(found, Retries, Max))
                {
                    if (look.AskWatcher()) Retries++;
                }
                else if (ConfigReloadGate.ShouldConfirmShrink(found, count, Count, stretch,
                    ConfigVanishConfirmer.DefaultFloor))
                {
                    if (Since is null) Since = elapsed;
                    look.Ask();
                }
                return false;
            }

            Count = count;
            Retries = 0;
            Since = null;
            Applied++;
            return true;
        }
    }

    /// <summary>
    /// Run every look the timer holds, advancing the clock by each one's
    /// delay, until it holds none or <paramref name="onFire"/>, the reload
    /// each look causes, applies.
    /// </summary>
    private void RunLooks(Func<bool> onFire, int cap = 20)
    {
        for (var i = 0; i < cap && _timer.Armed; i++)
        {
            _elapsed += _timer.LastDelay;
            _timer.Fire();
            if (onFire()) return;
        }
    }

    /// <summary>
    /// The second lockout of wintty#1155. A migrated user runs on two
    /// layered config files, deletes one, and has auto-reload-config off, so
    /// there is no watcher. Every load now finds one of two. That is a
    /// shrink, which is either a save in flight or a deletion, and only the
    /// stretch tells them apart; the confirmation used to count only asks
    /// the watcher took, so it never moved and every reload was refused
    /// until restart. Now the High Contrast toggle's reload is enough on its
    /// own: its looks run the stretch out to the floor at the service's own
    /// cadence and the shrink is applied as the deletion it is.
    /// </summary>
    [Fact]
    public void With_no_watcher_a_deleted_layered_file_concludes_through_its_own_looks()
    {
        var look = NoWatcher();
        var session = new Session { Count = 2 };

        // The toggle's reload: one of two, declined, and a look asked for.
        Assert.False(session.Reload(look, ConfigFilesFound.Loaded, 1, TimeSpan.Zero));
        Assert.True(_timer.Armed, "a declined shrink with no watcher scheduled no look");

        RunLooks(() => session.Reload(look, ConfigFilesFound.Loaded, 1, _elapsed));

        Assert.Equal(1, session.Applied);
        Assert.Equal(1, session.Count);
        Assert.Equal(4, session.Reloads);
        Assert.Equal(TimeSpan.FromMilliseconds(900), _elapsed);
        Assert.False(_timer.Armed, "an applied reload left a look armed");
    }

    /// <summary>
    /// A burst of watcher-taken deliveries inside one save stretch does
    /// not confirm the shrink. A High Contrast flip fires a train of
    /// colour-change events, every delivery takes the ask, and a count of
    /// asks was spent by such a burst within a single save: the remaining
    /// file applied, and the save's own settle restored the full config
    /// behind it (issue #1171). The stretch is measured, not counted, so
    /// the burst declines for as long as it is younger than the floor and
    /// the deletion applies only once the floor has run.
    /// </summary>
    [Fact]
    public void A_burst_of_watcher_taken_asks_inside_one_save_does_not_confirm_the_shrink()
    {
        var watcherAsks = 0;
        var look = new ConfigLookAgain(
            watcherAsk: () => { watcherAsks++; return true; }, _timer, onLook: () => { },
            maxDelay: ConfigVanishConfirmer.DefaultFloor);
        var session = new Session { Count = 2 };

        // Thirty deliveries 30ms apart: the widest measured save window is
        // 22ms, so this burst stays inside one save stretch even at the
        // measurement's own scale, and every one of them takes the ask.
        for (var i = 0; i < 30; i++)
            Assert.False(session.Reload(
                look, ConfigFilesFound.Loaded, 1,
                TimeSpan.FromMilliseconds(30 * i)));

        Assert.Equal(30, watcherAsks);
        Assert.Equal(0, session.Applied);
        Assert.False(_timer.Armed, "a taken ask left a look of the service's own armed");

        // The deletion is still there one save later, and the floor is
        // what concludes it, not the number of asks.
        Assert.True(session.Reload(
            look, ConfigFilesFound.Loaded, 1, ConfigVanishConfirmer.DefaultFloor));
        Assert.Equal(1, session.Applied);
        Assert.Equal(1, session.Count);
    }

    /// <summary>
    /// A stretch that the count restored mid-way is a fresh question: the
    /// save's own settle reloads with the full count, and the next shrink
    /// is measured from ITS first observation, not from the old stretch's.
    /// Without the reset the next shrink would arrive carrying the
    /// previous stretch's elapsed time and confirm on its first look.
    /// </summary>
    [Fact]
    public void A_count_the_reload_restored_starts_the_next_shrink_fresh()
    {
        var look = NoWatcher();
        var session = new Session { Count = 2 };

        // The shrink opens and asks.
        Assert.False(session.Reload(look, ConfigFilesFound.Loaded, 1, TimeSpan.Zero));

        // The save's settle: both files back, applied, stretch closed.
        Assert.True(session.Reload(look, ConfigFilesFound.Loaded, 2,
            TimeSpan.FromMilliseconds(300)));
        Assert.Null(session.Since);

        // A later deletion, far enough out that an unrestored stretch
        // would confirm it on sight.
        Assert.False(session.Reload(look, ConfigFilesFound.Loaded, 1,
            TimeSpan.FromMilliseconds(1000)));
        Assert.Equal(1, session.Applied);

        // Young again: it asks, waits out the floor from its own first
        // observation, and only then applies.
        Assert.False(session.Reload(look, ConfigFilesFound.Loaded, 1,
            TimeSpan.FromMilliseconds(1300)));
        Assert.True(session.Reload(look, ConfigFilesFound.Loaded, 1,
            TimeSpan.FromMilliseconds(1900)));
        Assert.Equal(2, session.Applied);
        Assert.Equal(1, session.Count);
    }

    /// <summary>
    /// A file that will not open gets NO look of the service's own. Every
    /// load of it has already blocked for the loader's sharing-violation
    /// retry, about four seconds on the UI thread, before answering
    /// Unreadable, so a look would be one more freeze: the three this budget
    /// once allowed turned a single High Contrast toggle into four freezes
    /// over about seventeen seconds. With no watcher the decline asks for
    /// nothing, spends nothing, and the declined request rides the next
    /// reload that comes for any other reason.
    /// </summary>
    [Fact]
    public void With_no_watcher_a_locked_file_takes_no_look_of_its_own()
    {
        var look = NoWatcher();
        var session = new Session { Count = 1 };

        Assert.False(session.Reload(look, ConfigFilesFound.Unreadable, 1, TimeSpan.Zero));

        Assert.False(_timer.Armed, "a locked file scheduled a look of the service's own");
        Assert.Equal(0, session.Retries);
        Assert.Equal(1, session.Reloads);
    }

    /// <summary>
    /// With a watcher, the locked file is retried through the watcher's own
    /// deliveries as before, and the budget stays bounded.
    /// </summary>
    [Fact]
    public void With_a_watcher_a_locked_file_is_retried_a_bounded_number_of_times()
    {
        var watcherAsks = 0;
        var look = new ConfigLookAgain(
            watcherAsk: () => { watcherAsks++; return true; }, _timer, onLook: () => { },
            maxDelay: ConfigVanishConfirmer.DefaultFloor);
        var session = new Session { Count = 1 };

        for (var i = 0; i < 10; i++)
            session.Reload(look, ConfigFilesFound.Unreadable, 1, TimeSpan.Zero);

        Assert.Equal(Max, session.Retries);
        Assert.Equal(Max, watcherAsks);
        Assert.False(_timer.Armed);
    }
    /// <summary>
    /// With a watcher that takes the ask, the service schedules nothing of
    /// its own: the watcher's delivery is the look.
    /// </summary>
    [Fact]
    public void The_service_looks_only_when_the_watcher_did_not()
    {
        var look = new ConfigLookAgain(
            watcherAsk: () => true, _timer, onLook: () => { },
            maxDelay: ConfigVanishConfirmer.DefaultFloor);

        Assert.True(look.Ask());
        Assert.False(_timer.Armed);
    }

    /// <summary>
    /// A look is never scheduled further out than the longest delay, nor
    /// before now. A delay computed from a clock that stepped back would
    /// otherwise leave the question waiting for the size of the step, and
    /// past about 49.7 days it throws inside the timer, inside Reload.
    /// </summary>
    [Fact]
    public void A_look_is_clamped_to_between_now_and_the_longest_delay()
    {
        var look = NoWatcher();

        Assert.True(look.Schedule(TimeSpan.FromDays(60)));
        Assert.Equal(ConfigVanishConfirmer.DefaultFloor, _timer.LastDelay);

        Assert.True(look.Schedule(TimeSpan.FromSeconds(-5)));
        Assert.Equal(TimeSpan.Zero, _timer.LastDelay);

        Assert.True(look.Schedule(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(TimeSpan.FromMilliseconds(250), _timer.LastDelay);
    }

    /// <summary>
    /// Stopped for teardown: a pending look is cancelled and no later ask
    /// schedules one, so nothing reloads into a freed app (issue #208).
    /// </summary>
    [Fact]
    public void A_stopped_look_cancels_and_schedules_nothing()
    {
        var look = NoWatcher();
        Assert.True(look.Schedule(TimeSpan.FromMilliseconds(300)));

        look.Stop();

        Assert.False(_timer.Armed);
        Assert.False(look.Ask());
        Assert.False(look.Schedule(TimeSpan.FromMilliseconds(300)));
        Assert.False(_timer.Armed);
    }

    private sealed class FakeTimer : ISchedulerTimer
    {
        public Action? Callback { get; set; }
        public bool Armed { get; private set; }
        public TimeSpan LastDelay { get; private set; }

        public void Schedule(TimeSpan delay)
        {
            LastDelay = delay;
            Armed = true;
        }

        public void Cancel() => Armed = false;

        public void Dispose() => Armed = false;

        public void Fire()
        {
            if (!Armed) return;
            Armed = false;
            Callback?.Invoke();
        }
    }
}
