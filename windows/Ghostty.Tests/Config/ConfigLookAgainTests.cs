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
/// that its decline branch counts <see cref="ConfigLookAgain.Ask"/> and
/// nothing else.
/// </remarks>
public class ConfigLookAgainTests
{
    // The service's MaxDeclinedReloadRetries and MaxShrinkConfirms.
    private const int Max = 3;

    private readonly FakeTimer _timer = new();
    private TimeSpan _elapsed;

    private ConfigLookAgain NoWatcher() =>
        new(watcherAsk: () => false, _timer, onLook: () => { },
            maxDelay: ConfigVanishConfirmer.DefaultFloor);

    /// <summary>
    /// A session and its decline branch, as the service has them.
    /// </summary>
    private sealed class Session
    {
        public int Count;
        public int Retries;
        public int Confirms;
        public int Reloads;
        public int Applied;

        public bool Reload(ConfigLookAgain look, ConfigFilesFound found, int count)
        {
            Reloads++;
            if (ConfigReloadGate.Decide(found, count, Count) == ConfigReloadDecision.Decline
                && !ConfigReloadGate.IsPersistentShrink(found, count, Count, Confirms, Max))
            {
                if (ConfigReloadGate.ShouldRetry(found, Retries, Max))
                {
                    if (look.Ask()) Retries++;
                }
                else if (ConfigReloadGate.ShouldConfirmShrink(found, count, Count, Confirms, Max))
                {
                    if (look.Ask()) Confirms++;
                }
                return false;
            }

            Count = count;
            Retries = 0;
            Confirms = 0;
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
    /// shrink, which is either a save in flight or a deletion, and only its
    /// budget of asks tells them apart; the budget used to count only asks
    /// the watcher took, so it never moved and every reload was refused
    /// until restart. Now the High Contrast toggle's reload is enough on its
    /// own: its looks spend the budget at the watcher's cadence and the
    /// shrink is applied as the deletion it is.
    /// </summary>
    [Fact]
    public void With_no_watcher_a_deleted_layered_file_concludes_through_its_own_looks()
    {
        var look = NoWatcher();
        var session = new Session { Count = 2 };

        // The toggle's reload: one of two, declined, and a look asked for.
        Assert.False(session.Reload(look, ConfigFilesFound.Loaded, 1));
        Assert.True(_timer.Armed, "a declined shrink with no watcher scheduled no look");

        RunLooks(() => session.Reload(look, ConfigFilesFound.Loaded, 1));

        Assert.Equal(1, session.Applied);
        Assert.Equal(1, session.Count);
        Assert.Equal(4, session.Reloads);
        Assert.Equal(TimeSpan.FromMilliseconds(900), _elapsed);
        Assert.False(_timer.Armed, "an applied reload left a look armed");
    }

    /// <summary>
    /// The same shape on the locked-file budget: a request declined because
    /// an editor or a scanner held the file is retried by the service's own
    /// looks, and lands once the file reads, rather than waiting for some
    /// unrelated reload with no watcher to ask.
    /// </summary>
    [Fact]
    public void With_no_watcher_a_locked_file_is_retried_through_its_own_looks()
    {
        var look = NoWatcher();
        var session = new Session { Count = 1 };

        Assert.False(session.Reload(look, ConfigFilesFound.Unreadable, 1));
        Assert.True(_timer.Armed, "a declined unreadable load with no watcher scheduled no look");

        // Still held at the first look; free by the second.
        var fires = 0;
        RunLooks(() => ++fires == 1
            ? session.Reload(look, ConfigFilesFound.Unreadable, 1)
            : session.Reload(look, ConfigFilesFound.Loaded, 1));

        Assert.Equal(1, session.Applied);
        Assert.Equal(TimeSpan.FromMilliseconds(600), _elapsed);
    }

    /// <summary>
    /// And the retries stay bounded: a file that never opens is looked at
    /// the budget's worth of times and then left alone, as with a watcher.
    /// </summary>
    [Fact]
    public void With_no_watcher_a_file_that_never_opens_is_looked_at_a_bounded_number_of_times()
    {
        var look = NoWatcher();
        var session = new Session { Count = 1 };

        session.Reload(look, ConfigFilesFound.Unreadable, 1);
        RunLooks(() => session.Reload(look, ConfigFilesFound.Unreadable, 1));

        Assert.Equal(0, session.Applied);
        Assert.Equal(1 + Max, session.Reloads);
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
