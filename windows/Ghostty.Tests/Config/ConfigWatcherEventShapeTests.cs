using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Ghostty.Core.Config;
using Ghostty.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The event-shape battery for the config watcher (the wintty#1158 rework):
/// atomic saves, delete-only, delete plus a late rename, slow-provider
/// shapes, driven as event SEQUENCES through the real watcher on a real
/// directory under %TEMP%, with the clock a test turns by hand.
/// </summary>
/// <remarks>
/// <para>These began life RED, against the shipped dwell: their assertions
/// encode the agreed contract, and against the 900ms load-verdict dwell
/// every one of them failed, because that dwell accepted a deletion 900ms
/// into any save's gap and applied the defaults config behind it. They run
/// the same wiring the service runs: the watcher, the event-shape machine,
/// the notice source and the vanish protocol, with a real
/// <see cref="NotificationService"/> holding the banner.</para>
///
/// <para>The watcher's EVENTS are raised by hand through
/// <see cref="ConfigFileWatcher.TestRaiseEvent"/> with the kind a real
/// FileSystemWatcher would report, never by the file system: waiting for
/// real events made every step a race the machine could lose (issue
/// #1161), and the gap between a delete and its rename is exactly the
/// thing under test, so it is a test-controlled clock here, not the
/// disk's. The real event stream keeps its own coverage over the save
/// shapes in ConfigFileWatcherTests.</para>
/// </remarks>
public sealed class ConfigWatcherEventShapeTests : IDisposable
{
    private const string FileName = "config.wintty";

    private readonly string _dir;
    private readonly string _path;
    private readonly FakeTimer _debounce = new();
    private readonly FakeTimer _backstop = new();
    private readonly Queue<Action> _post = new();
    private readonly NotificationService _notifications = new();
    private readonly ListLogger _log = new();

    // Event id from Ghostty.Core.Logging.LogEvents.Config.
    private const int WatcherError = 1009;

    // The session's recorded count, what RecordDefaultFiles owns in the
    // service: the contract says a deletion never moves it.
    private int _count = 1;
    private int _accepted;
    private int _settled;
    private int _vanished;
    private int _defaultsAsked;

    private TimeSpan _now = TimeSpan.FromHours(1);

    private ConfigFileWatcher? _watcher;
    private ConfigFilePresence? _presence;
    private ConfigFileMissingNoticeSource? _source;
    private ConfigVanishProtocol? _protocol;

    public ConfigWatcherEventShapeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "wintty-testcfg-shape-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// The service's wiring, verbatim where it matters: the machine feeds
    /// the notice source, the protocol feeds the same notice source on the
    /// paths that never see an event, a settled delivery reloads and a
    /// vanished one reloads, and an applied reload ends the notice. The
    /// pieces that are the SHELL's (the gate, the apply itself) are stood
    /// in for by counters, exactly the way ConfigVanishProtocolTests stood
    /// in for them before this battery existed.
    /// </summary>
    /// <param name="realEvents">Keep the real FileSystemWatcher enabled,
    /// for the one test that needs its Error event (the deleted directory).
    /// Everything else raises its events through the seam.</param>
    private void Start(bool realEvents = false)
    {
        _protocol = new ConfigVanishProtocol(
            sessionDefaultFilesFound: () => _count,
            ask: () => _watcher?.Resettle() == true,
            onAccept: () =>
            {
                Interlocked.Increment(ref _accepted);
                _source!.OnDeletionConfirmed();
            },
            now: () => _now);
        _presence = new ConfigFilePresence(now: Clock);
        // The poll timer is wired exactly as the service wires it: the
        // source arms it, its callback asks the machine. The dispatcher hop
        // the service adds is absent because this harness runs single
        // threaded on purpose.
        _backstop.Callback = () => _source!.OnBackstopPollDue();
        _source = new ConfigFileMissingNoticeSource(
            _presence,
            notifications: () => _notifications,
            scheduleBackstopPoll: delay =>
            {
                _backstop.Schedule(delay);
                return true;
            },
            cancelBackstopPoll: () => _backstop.Cancel(),
            loadDefaults: () => Interlocked.Increment(ref _defaultsAsked));
        _watcher = new ConfigFileWatcher(
            _path,
            _debounce,
            TimeSpan.FromMilliseconds(300),
            ignoreEvents: () => false,
            post: _post.Enqueue,
            onSettled: () =>
            {
                Interlocked.Increment(ref _settled);
                // A settle that reloads applies, in the service: the notice's
                // premise ends with the applied reload. See Reload's applied
                // path.
                _source!.OnReloadApplied();
            },
            _log,
            _presence,
            onVanished: () =>
            {
                Interlocked.Increment(ref _vanished);
                _protocol.Observed(ConfigFilesFound.Absent);
            });
        // The real FileSystemWatcher is built but usually never enabled:
        // every event is raised by hand through the seam, so no real event
        // can land beside the battery and re-arm the debounce behind the
        // test's back (issue #1161).
        if (!realEvents) _watcher.TestEnable = _ => { };
        Assert.True(_watcher.Start());
    }

    private TimeSpan Clock() => _now;

    /// <summary>Advance the test clock, then run the delivery the debounce
    /// armed, if one is pending at all. A stretch that arms nothing advances
    /// in silence, which is itself an assertion the callers make.</summary>
    private void Advance(TimeSpan at)
    {
        _now += at;
        if (!_debounce.Armed) return;
        _debounce.Fire();
        Assert.Single(_post);
        _post.Dequeue()();
    }

    /// <summary>The backstop poll the notice source armed, at the instant
    /// the clock has reached. A poll before any pending delete is a test
    /// bug, so it asserts the arming rather than guessing.</summary>
    private void PollBackstop()
    {
        Assert.True(_backstop.Armed, "no backstop poll was armed");
        _backstop.Fire();
    }

    // --- the save shapes -------------------------------------------------

    /// <summary>
    /// An atomic save whose rename lands long after the delete: the gap
    /// outlives the old 900ms floor several times over, as it can on a
    /// network or synced profile. The save reloads, and nothing in between
    /// concludes a deletion, touches the running config, or shows the
    /// notice.
    /// </summary>
    [Fact]
    public void An_atomic_save_past_the_old_floor_never_concludes_a_deletion()
    {
        Start();

        // The delete half, and the settle that lands inside the gap.
        File.Delete(_path);
        Assert.True(_watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted));
        Advance(TimeSpan.FromMilliseconds(300));

        // The gap: five seconds, no events, no reload, no notice.
        Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
        Assert.False(_source!.NoticeShowing);

        // The rename lands: its own event settles the save, and the settle
        // finds the file and reloads it.
        File.WriteAllText(Tmp, "font-size = 13\n");
        File.Move(Tmp, _path);
        Assert.True(_watcher.TestRaiseEvent(ConfigFileEventKind.Appeared));
        Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, _settled);
        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
        Assert.False(_source.NoticeShowing);
    }

    /// <summary>
    /// A delete with no reappearance, held well past any dwell that exists
    /// or ever existed: the running config still stands and nothing
    /// reloads. A deletion never resets the session to defaults; it is
    /// concluded by the backstop, and what it causes is the notice.
    /// </summary>
    [Fact]
    public void A_delete_alone_never_resets_the_running_config()
    {
        Start();

        File.Delete(_path);
        Assert.True(_watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted));

        // Watcher-cadence deliveries for ten seconds of test time: none of
        // them may turn into a conclusion about the session's config.
        for (var i = 0; i < 33; i++)
            Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
        Assert.Equal(0, _settled);
        Assert.Equal(0, _vanished);
    }

    /// <summary>
    /// A settle that lands inside a save's gap after a delete EVENT was
    /// seen has its answer already: the delete is pending, not absent. The
    /// shipped watcher reloaded into the gap and made the load's absence
    /// the dwell's first observation; a seen delete needs no reload to
    /// classify it.
    /// </summary>
    [Fact]
    public void A_seen_delete_needs_no_reload_to_classify()
    {
        Start();

        File.Delete(_path);
        Assert.True(_watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted));
        Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(0, _vanished);
        Assert.Equal(0, _settled);

        File.WriteAllText(Tmp, "font-size = 14\n");
        File.Move(Tmp, _path);
        Assert.True(_watcher.TestRaiseEvent(ConfigFileEventKind.Appeared));
        Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, _settled);
        Assert.Equal(0, _vanished);
    }

    /// <summary>The burst of one save, delete and rename together,
    /// collapses into one settle that sees the new content: the debounce
    /// property, with kinds.</summary>
    [Fact]
    public void A_save_burst_settles_once_with_the_new_content()
    {
        Start();

        File.WriteAllText(Tmp, "font-size = 13\n");
        Assert.True(_watcher!.TestRaiseEvent(ConfigFileEventKind.Changed));
        File.Delete(_path);
        Assert.True(_watcher.TestRaiseEvent(ConfigFileEventKind.Deleted));
        File.Move(Tmp, _path);
        Assert.True(_watcher.TestRaiseEvent(ConfigFileEventKind.Appeared));
        Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, _settled);
        Assert.Equal(0, _vanished);
        Assert.True(File.Exists(_path));
    }

    // --- the deletion branch ---------------------------------------------

    /// <summary>A delete past the backstop is a deletion: the notice goes
    /// up, the settings stand, and no reload happened to make it so.</summary>
    [Fact]
    public void A_delete_past_the_backstop_shows_the_notice_and_keeps_the_settings()
    {
        Start();

        File.Delete(_path);
        Assert.True(_watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted));
        Advance(TimeSpan.FromMilliseconds(300));

        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();

        Assert.True(_source!.NoticeShowing);
        var notice = Assert.Single(_notifications.Active);
        Assert.Equal(ConfigFileMissingNoticeSource.DedupKey, notice.DedupKey);
        Assert.Equal(0, _settled);
        Assert.Equal(0, _vanished);
        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
    }

    /// <summary>The notice's copy and action, exactly as the owner wrote
    /// them: reassurance first, and the one way out.</summary>
    [Fact]
    public void The_notice_says_what_the_owner_wrote_and_offers_the_defaults()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();

        var notice = Assert.Single(_notifications.Active);
        Assert.Equal("Config file missing", notice.Title);
        Assert.Equal("config file missing, still using the settings you had", notice.Message);
        Assert.Equal(NoticeSeverity.Informational, notice.Severity);
        Assert.True(notice.IsClosable);

        var action = Assert.Single(notice.Actions);
        Assert.Equal("load defaults", action.Label);
        Assert.True(action.IsPrimary);
        Assert.True(action.DismissesNotice);
    }

    /// <summary>A save that lands while the notice is up resolves the
    /// machine, settles, and takes the banner down: last event wins.</summary>
    [Fact]
    public void A_save_after_the_notice_takes_it_back_down()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();
        Assert.True(_source!.NoticeShowing);

        File.WriteAllText(Tmp, "font-size = 14\n");
        File.Move(Tmp, _path, overwrite: true);
        Assert.True(_watcher.TestRaiseEvent(ConfigFileEventKind.Appeared));
        Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, _settled);
        Assert.False(_source.NoticeShowing);
        Assert.Empty(_notifications.Active);
    }

    /// <summary>The action asks for defaults once and the banner goes with
    /// it. The host's two lines (invoke, then dismiss a dismissing action)
    /// are reproduced here because there is no window to click.</summary>
    [Fact]
    public void The_load_defaults_action_asks_once_and_takes_the_notice_down()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();

        var notice = Assert.Single(_notifications.Active);
        var action = Assert.Single(notice.Actions);

        action.Invoke();
        Assert.Equal(1, _defaultsAsked);
        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);

        // What NotificationHost.MakeButton does for a dismissing action.
        _notifications.Dismiss(notice);
        Assert.Empty(_notifications.Active);
        Assert.False(_source!.NoticeShowing);
    }

    /// <summary>A second deletion is news again: the user's dismissal does
    /// not silence the next episode, and the reappearance between them
    /// cleared the machine.</summary>
    [Fact]
    public void A_second_episode_after_the_file_returns_notifies_again()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();
        var first = Assert.Single(_notifications.Active);

        // The user closes it: through the service, so OnDismiss runs.
        _notifications.Dismiss(first);
        Assert.False(_source!.NoticeShowing);

        // The file comes back, and the next delete is a fresh episode.
        File.WriteAllText(_path, "font-size = 15\n");
        _watcher.TestRaiseEvent(ConfigFileEventKind.Appeared);
        Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, _settled);

        File.Delete(_path);
        _watcher.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();

        var second = Assert.Single(_notifications.Active);
        Assert.NotSame(first, second);
    }

    /// <summary>A provider that flaps, delete and rename in a tight loop,
    /// never sees the notice and never misses a save: every cycle resolves
    /// and settles.</summary>
    [Fact]
    public void A_flapping_provider_never_sees_the_notice()
    {
        Start();

        for (var i = 0; i < 5; i++)
        {
            File.Delete(_path);
            Assert.True(_watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted));
            Advance(TimeSpan.FromMilliseconds(300));

            File.WriteAllText(Tmp, $"font-size = {20 + i}\n");
            File.Move(Tmp, _path);
            Assert.True(_watcher.TestRaiseEvent(ConfigFileEventKind.Appeared));
            Advance(TimeSpan.FromMilliseconds(300));
        }

        Assert.Equal(5, _settled);
        Assert.False(_source!.NoticeShowing);
        Assert.Empty(_notifications.Active);
        Assert.Equal(0, _accepted);
    }

    /// <summary>Repeated delete reports inside one pending are the same
    /// news, not a new clock: the notice cannot be postponed by a provider
    /// that repeats itself.</summary>
    [Fact]
    public void Repeated_deletes_do_not_re_arm_the_backstop()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));

        Advance(ConfigFilePresence.DefaultBackstop - TimeSpan.FromSeconds(4));
        _watcher.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));

        // Ten seconds minus four have run since the FIRST delete; a re-arm
        // would leave this poll early and the notice would not come.
        Advance(TimeSpan.FromSeconds(4));
        PollBackstop();

        Assert.True(_source!.NoticeShowing);
    }

    /// <summary>A stale Changed report, a pre-delete write's buffered event
    /// arriving after the delete, does not resolve the pending: believing
    /// it would cancel a real deletion's notice.</summary>
    [Fact]
    public void A_stale_changed_event_does_not_resolve_a_pending_delete()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));

        _watcher.TestRaiseEvent(ConfigFileEventKind.Changed);
        Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(_source!.NoticeShowing);

        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();

        Assert.True(_source.NoticeShowing);
    }

    /// <summary>A settle that finds the file there resolves whatever the
    /// machine had open, even when the appearance's own event was lost: the
    /// existence check is live evidence, not a buffered report.</summary>
    [Fact]
    public void A_settle_that_finds_the_file_present_resolves_without_an_event()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));

        // The file comes back with no event the watcher heard (an overflow
        // ate it, or it arrived before the watch was rebuilt).
        File.WriteAllText(_path, "font-size = 16\n");
        Assert.True(_watcher.Resettle());
        Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, _settled);
        Assert.False(_source!.NoticeShowing);

        // And no notice comes later: the pending is gone.
        Advance(ConfigFilePresence.DefaultBackstop);
        Assert.False(_backstop.Armed, "a resolved pending left a poll armed");
    }

    /// <summary>The machine concluding and the protocol agreeing land on
    /// ONE banner: the watcher path's conclusion is deduped against the
    /// eventless path's, whichever lands first.</summary>
    [Fact]
    public void The_machine_and_the_protocol_agree_on_one_banner()
    {
        Start();

        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();
        Assert.True(_source!.NoticeShowing);

        // The eventless path's evidence arrives anyway: a reload somebody
        // else caused reports the absence twice, a floor apart, and the
        // protocol accepts it. The machine's clock and the protocol's are
        // the same test clock here, as they are the same wall clock in the
        // service, and the floor is measured from the first observation,
        // so it takes two.
        _now += ConfigVanishConfirmer.DefaultFloor;
        Assert.False(_protocol!.Observed(ConfigFilesFound.Absent));
        _now += ConfigVanishConfirmer.DefaultFloor;
        Assert.True(_protocol.Observed(ConfigFilesFound.Absent));
        Assert.Equal(1, _accepted);

        Assert.Single(_notifications.Active);
        Assert.True(_source.NoticeShowing);
    }

    /// <summary>
    /// A deleted watched DIRECTORY still concludes and still tells the
    /// user. The watcher dies with an Error, the rebuild ticks keep
    /// delivering while the directory is gone, and the machine, which saw
    /// the watched file's own delete report in the tree deletion, concludes
    /// at its backstop. The shipped version concluded here through load
    /// verdicts alone (#676's directory path); the ticks that carry the
    /// question are the same ticks.
    /// </summary>
    /// <remarks>
    /// A recursive directory delete raises the watched file's own delete
    /// report on this platform, which is what makes this deterministic;
    /// where a file system raises none, the same deletion concludes through
    /// the vanish protocol instead, and that path is the protocol battery's
    /// plus the rebuild-tick test in ConfigFileWatcherTests.
    /// </remarks>
    [Fact]
    public void A_deleted_directory_still_concludes_and_shows_the_notice()
    {
        // This member needs the real FileSystemWatcher: the Error event a
        // deleted watched directory raises is what schedules the rebuild.
        Start(realEvents: true);

        // The delete event of the file inside the tree, before the tree
        // goes: the machine's pending opens here, whatever the tree
        // deletion's own reports add, because a repeat delete re-arms
        // nothing.
        File.Delete(_path);
        _watcher!.TestRaiseEvent(ConfigFileEventKind.Deleted);
        Advance(TimeSpan.FromMilliseconds(300));

        Retry(() => Directory.Delete(_dir, recursive: true));
        // Wait on the ERROR, not on the timer: the tree deletion also
        // raises the watched file's own delete report, which arms the
        // debounce exactly like the error's rebuild schedule does, and a
        // wait on the flag can succeed on the wrong one.
        WaitUntil(() => _log.Count(WatcherError) == 1,
            "the watcher reported no error for its deleted directory");

        // The rebuild fails while the directory is missing, and the tick
        // delivers anyway: that delivery is the liveness #676 hung on, and
        // under the machine it reports nothing, because the delete is
        // already being classified.
        Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, _accepted);
        Assert.Equal(0, _settled);
        Assert.True(_debounce.Armed, "the failed watcher stopped retrying");

        // Past the backstop, the deletion concludes and the notice goes up.
        // The settings stand.
        Advance(ConfigFilePresence.DefaultBackstop);
        PollBackstop();

        Assert.True(_source!.NoticeShowing);
        Assert.Single(_notifications.Active);
        Assert.Equal(1, _count);
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        // Generous hang guard: the deadline exists so a broken condition
        // fails instead of hanging the suite, not to measure the machine.
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), failure);
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
                                       && sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Counts log entries by event id, the way
    /// ConfigFileWatcherTests's does: enough to know the watcher reported
    /// its deleted directory, and nothing more.</summary>
    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly ConcurrentQueue<int> _entries = new();

        public int Count(int id) => _entries.Count(e => e == id);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(eventId.Id);
    }

    /// <summary>Fires only when armed, as the real timer does, so a test
    /// cannot deliver what the watcher never scheduled.</summary>
    private sealed class FakeTimer : ISchedulerTimer
    {
        private volatile bool _armed;
        private long _lastDelayTicks;

        public Action? Callback { get; set; }
        public bool Armed => _armed;
        public bool Disposed { get; private set; }
        public TimeSpan LastDelay => TimeSpan.FromTicks(Interlocked.Read(ref _lastDelayTicks));

        public void Schedule(TimeSpan delay)
        {
            Interlocked.Exchange(ref _lastDelayTicks, delay.Ticks);
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
    }
}
