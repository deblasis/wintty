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

    private void Start(Func<bool>? ask = null)
    {
        // The service's own wiring, verbatim: the protocol owns the
        // budget, the ask goes through the watcher's Resettle, and the
        // accept is the count dropping to zero.
        var protocol = new ConfigVanishProtocol(
            sessionDefaultFilesFound: () => _count,
            ask: ask ?? (() => _watcher?.Resettle() == true),
            onAccept: () =>
            {
                Interlocked.Increment(ref _accepted);
                _count = 0;
            });
        _watcher = new ConfigFileWatcher(
            _path,
            _timer,
            TimeSpan.FromMilliseconds(300),
            ignoreEvents: () => _ignore,
            post: _post.Enqueue,
            onSettled: () =>
            {
                protocol.Settled();
                Interlocked.Increment(ref _settled);
            },
            NullLogger<ConfigFileWatcher>.Instance,
            onVanished: () =>
            {
                Interlocked.Increment(ref _vanished);
                protocol.Vanished();
            });
        Assert.True(_watcher.Start());
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
    /// inside its swap gap, and a budget still spent accepted that one
    /// report at once, zeroing the count mid save. That is #1146
    /// re-entered through the very budget added to prevent it.
    /// </summary>
    [Fact]
    public void A_return_through_the_apps_own_write_leaves_no_stale_budget()
    {
        Start();

        // 1. A real deletion, confirmed through the watcher's own
        // deliveries: three asks, then the report past the budget.
        _timer.WaitForBurst(() => File.Delete(_path));
        for (var i = 0; i < 3; i++)
        {
            FireAndDeliver();
            Assert.True(_timer.Armed, "the ask scheduled no next delivery");
        }
        FireAndDeliver();
        Assert.Equal(1, _accepted);
        Assert.Equal(0, _count);
        Assert.False(_timer.Armed, "an accept asked again");

        // 2. The file returns the way the settings UI returns it: the
        // app's own write, bracketed by suppression, so its events arm
        // nothing and no settle ever runs. The reload the write triggers
        // applies and records the count, which is the app's own evidence
        // the file is back.
        _ignore = true;
        WriteLikeTheApp("font-size = 13\n");
        Thread.Sleep(400);
        _count = 1;
        _ignore = false;
        Assert.Empty(_post);
        Assert.False(_timer.Armed, "a suppressed write armed the watcher");

        // 3. An external editor's ordinary save, with the delivery landing
        // in the swap gap: the delete arms the debounce, the delivery runs
        // before the rename lands. One report, mid save: the #1146 shape.
        _timer.WaitForBurst(() => File.Delete(_path));
        Assert.True(_timer.Armed);
        _timer.Fire();
        Assert.Single(_post);
        _post.Dequeue()();

        // The budget did not survive its own conclusion: the mid-save
        // report asks again rather than accepting, the count stands, and
        // an ask was actually scheduled.
        Assert.Equal(1, _accepted);
        Assert.Equal(1, _count);
        Assert.True(_timer.Armed, "the fresh ask scheduled nothing");

        // 4. The swap completes, and the settle that reports it is where
        // the budget is restored the ordinary way.
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
    /// The ask is what keeps a real deletion moving: every report
    /// schedules the next delivery through the watcher's own Resettle,
    /// and the chain concludes on its own. This is the liveness the
    /// shell's wiring pins the ask to.
    /// </summary>
    [Fact]
    public void A_real_deletion_concludes_through_the_watchers_own_deliveries()
    {
        Start();

        _timer.WaitForBurst(() => File.Delete(_path));
        for (var i = 0; i < 3; i++) FireAndDeliver();
        FireAndDeliver();

        // Four reports: three the asks bought and the one past them.
        Assert.Equal(4, _vanished);
        Assert.Equal(1, _accepted);
        Assert.Equal(0, _count);
    }

    /// <summary>
    /// And this is the wedge that liveness pin is against: an ask wired
    /// to a constant spends nothing and concludes nothing, so a deletion
    /// is never confirmed and the count never lowers. In the shell that
    /// is every later reload declined for the life of the process, the
    /// #676 lockout, restored by a mutation nothing else would see.
    /// </summary>
    [Fact]
    public void An_ask_that_schedules_nothing_never_concludes()
    {
        Start(ask: () => false);

        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();

        // One report, no ask taken, nothing scheduled: the chain ends
        // here and nothing will ever revisit the question.
        Assert.False(_timer.Armed);
        Assert.Equal(0, _accepted);
        Assert.Equal(1, _count);
    }

    /// <summary>
    /// The settle is where the file is seen present, and it restores the
    /// budget: a stretch interrupted by the file coming back is asked in
    /// full again, not continued. A budget carried across the return had
    /// the next save's first report accepted at once, #1146 through a
    /// stale budget from the other direction.
    /// </summary>
    [Fact]
    public void A_settle_restores_the_budget_so_the_next_stretch_asks_in_full()
    {
        Start();

        // Half a stretch: two of three asks spent on a deletion.
        _timer.WaitForBurst(() => File.Delete(_path));
        FireAndDeliver();
        FireAndDeliver();

        // The file comes back, and the settle sees it present.
        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 15\n"));
        FireAndDeliver();
        Assert.Equal(1, _settled);
        Assert.Equal(0, _accepted);

        // The next deletion is a fresh question, asked in full.
        _timer.WaitForBurst(() => File.Delete(_path));
        for (var i = 0; i < 3; i++)
        {
            FireAndDeliver();
            Assert.Equal(0, _accepted);
        }
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

        for (var i = 0; i < 3; i++)
        {
            FireAndDeliver();
            Assert.True(_timer.Armed, "the ask scheduled no next delivery");
        }
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
