using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Ghostty.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The vanish protocol end to end: a real <c>ConfigFileWatcher</c> on a
/// real directory under %TEMP%, the session's own writes suppressed the
/// way the service suppresses them, and every delivery driven by hand
/// through the ask chain.
/// </summary>
/// <remarks>
/// The service wires exactly this and nothing executes the service, so
/// these sequences lived only as source shape until the wiring moved into
/// Ghostty.Core. Every assertion here runs the real thing: real events,
/// real debounces, real deliveries, real asks.
/// </remarks>
public sealed class ConfigVanishProtocolTests : IDisposable
{
    private const string FileName = "config.wintty";

    private readonly string _dir;
    private readonly string _path;
    private readonly FakeTimer _timer = new();
    private readonly Queue<Action> _post = new();

    // The service's _suppressWatcher: set around its own writes. The
    // watcher consults it when each event arrives, so those writes raise
    // events that arm nothing and settle nothing.
    private volatile bool _ignore;

    // The session's recorded count, what RecordDefaultFiles owns in the
    // service: raised by an applied load, lowered to zero by an accept.
    private int _count = 1;
    private int _accepted;
    private int _settled;
    private int _vanished;

    private ConfigFileWatcher? _watcher;

    public ConfigVanishProtocolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "wintty-testcfg-vanish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, FileName);
        File.WriteAllText(_path, "font-size = 12\n");
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Tmp => Path.Combine(_dir, FileName + ".tmp");

    private TimeSpan _now = TimeSpan.FromHours(1);

    /// <summary>
    /// Move the test clock past the floor, which is what a real stretch of
    /// absence does by taking wall time. Nothing here sleeps.
    /// </summary>
    private void AdvancePastFloor() => _now += ConfigVanishConfirmer.DefaultFloor;

    /// <summary>
    /// Wire the watcher to a protocol the way the service does, and hand
    /// the protocol back: a host with no watcher still reloads, and a test
    /// needs to be able to deliver that verdict without a delivery.
    /// </summary>
    private ConfigVanishProtocol Start(Func<bool>? ask = null)
    {
        // The service's own wiring, verbatim. The protocol is driven by what
        // a LOAD found and not by the delivery that prompted it, so each
        // callback stands in for the reload it causes: a settled delivery
        // reloads and the file reads, a vanished one reloads and it does
        // not. ConfigService does exactly this, one level up, with the
        // verdict coming from BuildLiveConfig.
        var protocol = new ConfigVanishProtocol(
            sessionDefaultFilesFound: () => _count,
            ask: ask ?? (() => _watcher?.Resettle() == true),
            onAccept: () =>
            {
                Interlocked.Increment(ref _accepted);
                _count = 0;
            },
            now: () => _now);
        _watcher = new ConfigFileWatcher(
            _path,
            _timer,
            TimeSpan.FromMilliseconds(300),
            ignoreEvents: () => _ignore,
            post: _post.Enqueue,
            onSettled: () =>
            {
                protocol.Observed(ConfigFilesFound.Loaded);
                Interlocked.Increment(ref _settled);
            },
            NullLogger<ConfigFileWatcher>.Instance,
            onVanished: () =>
            {
                Interlocked.Increment(ref _vanished);
                protocol.Observed(ConfigFilesFound.Absent);
            });
        Assert.True(_watcher.Start());
        return protocol;
    }

    /// <summary>
    /// Fire the debounce and run the one delivery it posted. Each tick
    /// posts exactly one delivery, so a tick that posted none is a wedge,
    /// not a lull.
    /// </summary>
    private void FireAndDeliver()
    {
        _timer.Fire();
        Assert.Single(_post);
        _post.Dequeue()();
    }

    /// <summary>
    /// The app's own write, the ConfigFileEditor.WriteAtomic primitives:
    /// it recreates the file from nothing, which is how the settings UI
    /// returns a config a deletion took away, and its events are the
    /// suppressed ones no settle ever sees.
    /// </summary>
    private void WriteLikeTheApp(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Tmp, content);
        File.Move(Tmp, _path, overwrite: true);
    }

    /// <summary>
    /// The adversarial sequence the first cut of the protocol failed: a
    /// real deletion spends the budget and is accepted; the file returns
    /// through the app's OWN write, whose events are suppressed, so no
    /// settle runs and the count comes back only through the reload the
    /// write triggers; the next ordinary save then reports once from
    /// inside its swap gap, and a stretch still open accepted that one
    /// report at once, zeroing the count mid save. That is #1146
    /// re-entered through the very state added to prevent it.
    ///
    /// It is also where driving the protocol by LOAD VERDICTS rather than
    /// by watcher deliveries earns its keep: the app's own write raises
    /// only suppressed events, so nothing settles, and a stretch that
    /// ended on deliveries would still be open here.
    /// </summary>
    [Fact]
    public void A_return_through_the_apps_own_write_leaves_no_stale_stretch()
    {
        var protocol = Start();

        // 1. A real deletion, confirmed through the watcher's own
        // deliveries: the report that opens the stretch, then the one
        // past the floor.
        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();
        Assert.Equal(0, _accepted);
        Assert.True(_timer.Armed, "the ask scheduled no next delivery");

        AdvancePastFloor();
        FireAndDeliver();
        Assert.Equal(1, _accepted);
        Assert.Equal(0, _count);
        Assert.False(_timer.Armed, "an accept asked again");

        // 2. The file returns the way the settings UI returns it: the
        // app's own write, bracketed by suppression, so its events arm
        // nothing and no settle ever runs. The reload the write triggers
        // is the app's own evidence the file is back: it records the
        // count, and its VERDICT is what ends the open stretch.
        _ignore = true;
        WriteLikeTheApp("font-size = 13\n");
        Thread.Sleep(400);
        _count = 1;
        Assert.False(protocol.Observed(ConfigFilesFound.Loaded));
        _ignore = false;
        Assert.Empty(_post);
        Assert.False(_timer.Armed, "a suppressed write armed the watcher");

        // 3. An external editor's ordinary save, with the delivery landing
        // in the swap gap: the delete arms the debounce, the delivery runs
        // before the rename lands. One report, mid save: the #1146 shape.
        // The clock does not move: the widest save gap measured is 22ms,
        // and the floor is 900ms. What would make this report conclude is
        // not elapsed time here but the stretch from step 1 still being
        // open, whose first observation is already a floor in the past.
        _timer.WaitForBurst(() => File.Delete(_path));
        Assert.True(_timer.Armed);
        _timer.Fire();
        Assert.Single(_post);
        _post.Dequeue()();

        // The stretch survived neither its own conclusion nor the return:
        // the mid-save report opens a fresh one rather than accepting, the
        // count stands, and an ask was actually scheduled.
        Assert.Equal(1, _accepted);
        Assert.Equal(1, _count);
        Assert.True(_timer.Armed, "the fresh ask scheduled nothing");

        // 4. The swap completes, and the settle that reports it is where
        // the stretch ends the ordinary way.
        _timer.WaitForBurst(() =>
        {
            File.WriteAllText(Tmp, "font-size = 14\n");
            File.Move(Tmp, _path);
        });
        FireAndDeliver();
        Assert.Equal(1, _settled);
        Assert.Equal(1, _accepted);
        Assert.Equal(1, _count);
    }

    /// <summary>
    /// The ask is what keeps a real deletion moving: a report that cannot
    /// yet conclude schedules the next delivery through the watcher's own
    /// Resettle, so the question is revisited with nobody touching
    /// anything and the stretch reaches the floor on its own. This is the
    /// liveness the shell's wiring pins the ask to.
    /// </summary>
    [Fact]
    public void A_real_deletion_concludes_through_the_watchers_own_deliveries()
    {
        Start();

        _timer.WaitForBurst(() => File.Delete(_path));

        // The first report opens the stretch and cannot conclude it: the
        // floor is measured from this instant, so nothing has elapsed.
        FireAndDeliver();
        Assert.Equal(0, _accepted);
        Assert.True(_timer.Armed, "the ask scheduled no next delivery");

        // The stretch runs past the floor, which in a session is wall time
        // and here is the test clock. Nothing sleeps.
        AdvancePastFloor();
        FireAndDeliver();

        Assert.Equal(2, _vanished);
        Assert.Equal(1, _accepted);
        Assert.Equal(0, _count);
        Assert.False(_timer.Armed, "an accept asked again");
    }

    /// <summary>
    /// A file that is there is never confirmed gone, however long the
    /// session runs and however often it is loaded.
    /// </summary>
    /// <remarks>
    /// This is the half of the verdict rule that no deletion sequence can
    /// see. Every one of those starts from a real absence, so swapping which
    /// verdict opens a stretch breaks them for a reason that almost any
    /// other broken rule also produces, and the swap has no test of its own.
    /// Here the file never goes away, so a conclusion can only come from
    /// treating a load that FOUND it as evidence that it is gone.
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

        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();

        // Nothing was scheduled, so no delivery will revisit this. One
        // report proves nothing either way, so the count still stands.
        Assert.False(_timer.Armed);
        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);

        // The next observation arrives the way it does when there is no
        // watcher at all: some other reload, a theme change or a settings
        // edit, builds the config and finds no default file. Past the
        // floor, that is proof, and the session stops claiming the file.
        AdvancePastFloor();
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);
        Assert.Equal(0, _count);
    }

    /// <summary>
    /// The protocol as a host with no watcher wires it: the default, since
    /// <c>auto-reload-config</c> is off. The ask never schedules anything,
    /// and every look it takes instead is recorded with its delay.
    /// </summary>
    private ConfigVanishProtocol NoWatcher(System.Collections.Generic.List<TimeSpan> looks) =>
        new(
            sessionDefaultFilesFound: () => _count,
            ask: () => false,
            onAccept: () =>
            {
                Interlocked.Increment(ref _accepted);
                _count = 0;
            },
            now: () => _now,
            lookAgainAfter: delay =>
            {
                looks.Add(delay);
                return true;
            });

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
    /// (wintty#1155).
    /// </remarks>
    [Fact]
    public void With_no_watcher_one_reload_is_enough_because_the_protocol_looks_again()
    {
        var looks = new System.Collections.Generic.List<TimeSpan>();
        var protocol = NoWatcher(looks);

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
        Assert.Equal(0, _count);
        Assert.Single(looks);
    }

    /// <summary>
    /// The look is scheduled only when the watcher took no ask. With a
    /// watcher, its own deliveries already come every 300ms and a second
    /// source of reloads would only double the rebuilds.
    /// </summary>
    [Fact]
    public void The_look_again_is_not_taken_when_the_watcher_took_the_ask()
    {
        var looks = new System.Collections.Generic.List<TimeSpan>();
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
        var looks = new System.Collections.Generic.List<TimeSpan>();
        var protocol = NoWatcher(looks);

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
    /// No look once there is nothing left to lose. After an accepted
    /// deletion the session count is zero, and every later reload finds the
    /// same absence; a look scheduled from there would find it too and
    /// schedule another, a reload every floor for the life of the process.
    /// The same holds from startup under --no-config, where the count is
    /// pinned at zero.
    /// </summary>
    [Fact]
    public void No_look_is_scheduled_once_the_session_has_no_config_file()
    {
        var looks = new System.Collections.Generic.List<TimeSpan>();
        var protocol = NoWatcher(looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += looks[0];
        Assert.True(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(0, _count);

        for (var i = 0; i < 3; i++)
        {
            _now += ConfigVanishConfirmer.DefaultFloor;
            Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        }
        Assert.Single(looks);

        // And a session that never had one asks for nothing from the start.
        var none = new System.Collections.Generic.List<TimeSpan>();
        _count = 0;
        var fresh = NoWatcher(none);
        Assert.False(fresh.Observed(ConfigFilesFound.Absent));
        Assert.Empty(none);
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
        var looks = new System.Collections.Generic.List<TimeSpan>();
        var protocol = NoWatcher(looks);

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
        var looks = new System.Collections.Generic.List<TimeSpan>();
        var protocol = NoWatcher(looks);

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
    /// With a watcher, a deletion concludes through deliveries a debounce
    /// apart, and every one short of the floor has to ask for the next: one
    /// that asked nothing would leave the watcher quiet and the question
    /// waiting for an unrelated reload, the wintty#1155 symptom down the
    /// watcher's path. The other watcher tests jump a whole floor in one
    /// step, so they never see an intermediate delivery.
    /// </summary>
    [Fact]
    public void With_a_watcher_every_delivery_short_of_the_floor_asks_for_the_next()
    {
        Start();
        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();

        var step = TimeSpan.FromMilliseconds(300);
        for (var i = 0; i < 2; i++)
        {
            _now += step;
            FireAndDeliver();
            Assert.Equal(0, _accepted);
            Assert.True(_timer.Armed, "an intermediate delivery asked nothing");
        }

        _now += step;
        FireAndDeliver();
        Assert.Equal(1, _accepted);
    }

    /// <summary>
    /// A file that is there but will not open ends the stretch like one that
    /// reads. A lock, a sharing violation, an offline cloud placeholder or a
    /// scanner's hold all come back Unreadable, not Absent. Keeping the
    /// stretch open across one would let a later load in another save's gap
    /// accept on that single observation, its first already a floor old,
    /// and apply defaults mid save: #1146.
    /// </summary>
    [Fact]
    public void An_unreadable_verdict_ends_the_stretch()
    {
        var looks = new System.Collections.Generic.List<TimeSpan>();
        var protocol = NoWatcher(looks);

        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.False(protocol.Observed(ConfigFilesFound.Unreadable));
        AdvancePastFloor();
        Assert.False(protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(0, _accepted);
    }

    /// <summary>
    /// The settle's reload proves a file is there, and that ends the
    /// stretch: a run of absence interrupted by the file coming back
    /// starts from nothing, not from where it left off. A stretch carried
    /// across the return had the next save's first report accepted at
    /// once, however long ago that first observation was, which is #1146
    /// through stale state from the other direction.
    /// </summary>
    [Fact]
    public void A_settle_ends_the_stretch_so_the_next_one_starts_from_nothing()
    {
        Start();

        // A stretch opened on a deletion, not yet past the floor.
        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();

        // The file comes back, and the settle's reload finds it.
        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 15\n"));
        FireAndDeliver();
        Assert.Equal(1, _settled);
        Assert.Equal(0, _accepted);

        // Time passes, as it does in any session. Were the stretch still
        // open, this alone would make the next single report conclusive.
        AdvancePastFloor();

        // The next deletion is a fresh question: its first report proves
        // nothing, however long the previous stretch ran.
        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();
        Assert.Equal(0, _accepted);

        AdvancePastFloor();
        FireAndDeliver();
        Assert.Equal(1, _accepted);
    }

    /// <summary>
    /// A deleted watched DIRECTORY concludes too: its rebuild ticks fail
    /// while it is missing, and each still delivers, which is what
    /// carries the question to its answer. Without the delivery the
    /// question stalls unconfirmed and every later reload declines for
    /// the life of the process, the #676 lockout down the directory path.
    /// </summary>
    [Fact]
    public void A_deleted_directory_concludes_through_its_rebuild_ticks()
    {
        Start();

        Retry(() => Directory.Delete(_dir, recursive: true));
        WaitUntil(() => _timer.Armed,
            "the failed watcher scheduled no rebuild");

        FireAndDeliver();
        Assert.Equal(0, _accepted);
        Assert.True(_timer.Armed, "the ask scheduled no next delivery");

        AdvancePastFloor();
        FireAndDeliver();

        Assert.Equal(1, _accepted);
        Assert.Equal(0, _count);
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), failure);
            Thread.Sleep(10);
        }
    }

    // Antivirus or the indexer can hold a handle for a moment.
    private static void Retry(Action act)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                act();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Fires only when armed, as the real timer does, so a test cannot
    /// settle an edit the watcher never scheduled.
    /// </summary>
    private sealed class FakeTimer : ISchedulerTimer
    {
        private int _scheduleCount;
        private volatile bool _armed;
        private long _lastDelayTicks;

        public Action? Callback { get; set; }
        public bool Armed => _armed;
        public bool Disposed { get; private set; }
        public int ScheduleCount => Volatile.Read(ref _scheduleCount);
        public TimeSpan LastDelay => TimeSpan.FromTicks(Interlocked.Read(ref _lastDelayTicks));

        public void Schedule(TimeSpan delay)
        {
            Interlocked.Exchange(ref _lastDelayTicks, delay.Ticks);
            Interlocked.Increment(ref _scheduleCount);
            _armed = true;
        }

        public void Cancel() => _armed = false;

        public void Dispose()
        {
            _armed = false;
            Disposed = true;
        }

        public void Fire()
        {
            if (!_armed) return;
            _armed = false;
            Callback?.Invoke();
        }

        /// <summary>
        /// Run <paramref name="act"/>, then wait until the events it caused
        /// have arrived and gone quiet for longer than any gap inside one
        /// save, so the burst is complete before the test settles it.
        /// </summary>
        public void WaitForBurst(Action act)
        {
            var before = ScheduleCount;
            act();

            var sw = Stopwatch.StartNew();
            while (ScheduleCount == before)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                    "the file operation raised no watcher event within 5s");
                Thread.Sleep(10);
            }

            var last = ScheduleCount;
            var quiet = Stopwatch.StartNew();
            while (quiet.Elapsed < TimeSpan.FromMilliseconds(250))
            {
                Thread.Sleep(10);
                var now = ScheduleCount;
                if (now != last)
                {
                    last = now;
                    quiet.Restart();
                }
            }
        }
    }
}
