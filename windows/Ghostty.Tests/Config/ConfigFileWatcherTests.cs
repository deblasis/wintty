using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Ghostty.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The config watcher against the save shapes editors really use, on a
/// real directory under %TEMP% (never a real config). The debounce timer is
/// a fake so each test decides when the edit settles; the file system
/// events are real, so each step waits for the burst it caused to arrive
/// and go quiet before settling.
/// </summary>
public sealed class ConfigFileWatcherTests : IDisposable
{
    private const string FileName = "config.wintty";

    // Event ids from Ghostty.Core.Logging.LogEvents.Config.
    private const int WatcherError = 1009;
    private const int WatcherFileMissing = 1010;
    private const int WatcherOverflow = 1011;
    private const int WatcherErrorRepeat = 1012;
    private const int WatcherRebuilt = 1013;

    private readonly string _dir;
    private readonly string _path;
    private readonly FakeTimer _timer = new();
    private readonly ListLogger _log = new();
    private bool _ignore;
    private int _settled;
    private string? _contentAtSettle;

    public ConfigFileWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "wintty-testcfg-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, FileName);
        File.WriteAllText(_path, "font-size = 12\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <param name="post">Defaults to running the delivery inline, as if
    /// the timer's thread were the loading thread.</param>
    private ConfigFileWatcher NewWatcher(Action<Action>? post = null)
    {
        var w = new ConfigFileWatcher(
            _path,
            _timer,
            TimeSpan.FromMilliseconds(300),
            ignoreEvents: () => _ignore,
            post: post ?? (deliver => deliver()),
            onSettled: () =>
            {
                Interlocked.Increment(ref _settled);
                _contentAtSettle = File.ReadAllText(_path);
            },
            _log);
        Assert.True(w.Start());
        return w;
    }

    private string Tmp => Path.Combine(_dir, FileName + ".tmp");

    public static TheoryData<string> SaveShapes() => new()
    {
        "in-place",
        "copy-over",
        "delete-then-rename",
        "replace-file-with-backup",
        "replace-file-without-backup",
        "move-overwrite",
        "rename-away-then-write",
    };

    private void Save(string shape, string content)
    {
        switch (shape)
        {
            case "in-place":
                File.WriteAllText(_path, content);
                break;
            case "copy-over":
                File.WriteAllText(Tmp, content);
                File.Copy(Tmp, _path, overwrite: true);
                File.Delete(Tmp);
                break;
            case "delete-then-rename":
                File.WriteAllText(Tmp, content);
                File.Delete(_path);
                File.Move(Tmp, _path);
                break;
            case "replace-file-with-backup":
                File.WriteAllText(Tmp, content);
                File.Replace(Tmp, _path, _path + ".bak");
                File.Delete(_path + ".bak");
                break;
            case "replace-file-without-backup":
                File.WriteAllText(Tmp, content);
                File.Replace(Tmp, _path, destinationBackupFileName: null);
                break;
            case "move-overwrite":
                File.WriteAllText(Tmp, content);
                File.Move(Tmp, _path, overwrite: true);
                break;
            case "rename-away-then-write":
                File.Move(_path, _path + "~");
                File.WriteAllText(_path, content);
                File.Delete(_path + "~");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    [Theory]
    [MemberData(nameof(SaveShapes))]
    public void Every_save_shape_settles_once_with_the_new_content(string shape)
    {
        using var watcher = NewWatcher();

        _timer.WaitForBurst(() => Save(shape, "font-size = 13\n"));
        _timer.Fire();

        Assert.Equal(1, _settled);
        Assert.Equal("font-size = 13\n", _contentAtSettle);
    }

    [Theory]
    [MemberData(nameof(SaveShapes))]
    public void The_watcher_survives_a_replace_and_sees_the_next_save(string shape)
    {
        using var watcher = NewWatcher();

        _timer.WaitForBurst(() => Save(shape, "font-size = 13\n"));
        _timer.Fire();
        _timer.WaitForBurst(() => Save(shape, "font-size = 14\n"));
        _timer.Fire();
        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 15\n"));
        _timer.Fire();

        Assert.Equal(3, _settled);
        Assert.Equal("font-size = 15\n", _contentAtSettle);
    }

    [Fact]
    public void A_settle_inside_the_swap_gap_reports_nothing_and_the_completed_swap_reports_once()
    {
        using var watcher = NewWatcher();

        // The first half of ReplaceFile / a backup-rename save: the file is
        // gone and its event has armed the debounce. An editor that takes
        // longer than the debounce to land the new file lets it fire here.
        _timer.WaitForBurst(() => File.Move(_path, _path + "~"));
        Assert.True(_timer.Armed);
        _timer.Fire();

        Assert.Equal(0, _settled);
        Assert.False(File.Exists(_path));

        // The second half lands: its own event re-arms, and that settle
        // finds the file.
        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 16\n"));
        _timer.Fire();

        Assert.Equal(1, _settled);
        Assert.Equal("font-size = 16\n", _contentAtSettle);
    }

    [Fact]
    public void A_delete_arms_the_debounce_so_a_pending_settle_cannot_land_in_the_gap()
    {
        using var watcher = NewWatcher();

        _timer.WaitForBurst(() => File.Delete(_path));

        Assert.True(_timer.Armed);
        _timer.Fire();
        Assert.Equal(0, _settled);
    }

    [Fact]
    public void The_file_check_runs_in_the_delivery_not_on_the_timer()
    {
        // The delivery is queued, as the UI dispatcher queues it. The file
        // is present when the debounce fires and gone by the time the
        // delivery runs: the check that counts is the one next to the load.
        var queue = new Queue<Action>();
        using var watcher = NewWatcher(post: queue.Enqueue);

        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 22\n"));
        _timer.Fire();
        Assert.Single(queue);
        Assert.Equal(0, _settled);

        File.Move(_path, _path + "~");
        queue.Dequeue()();

        Assert.Equal(0, _settled);
        Assert.Equal(1, _log.Count(WatcherFileMissing));
    }

    [Fact]
    public void A_delivery_queued_before_dispose_does_nothing()
    {
        var queue = new Queue<Action>();
        var watcher = NewWatcher(post: queue.Enqueue);

        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 23\n"));
        _timer.Fire();
        watcher.Dispose();
        queue.Dequeue()();

        Assert.Equal(0, _settled);
    }

    [Fact]
    public void The_watcher_is_rebuilt_after_its_directory_is_deleted_and_recreated()
    {
        using var watcher = NewWatcher();

        // Deleting the watched directory ends the watcher with an Error that
        // is not an overflow. The watcher is dead from then on, so recovery
        // has to build a new one.
        DeleteDirectory(_dir);
        WaitUntil(() => _log.Count(WatcherError) == 1, "the watcher reported no error for its deleted directory");
        Assert.True(_timer.Armed);

        // Still missing: the attempt fails and schedules another.
        _timer.Fire();
        Assert.Equal(0, _settled);
        Assert.True(_timer.Armed);

        // The directory and the file come back while nothing is watching.
        // The next attempt rebuilds, and settles once for what it missed.
        RecreateDirectory(_dir);
        File.WriteAllText(_path, "font-size = 20\n");
        _timer.Fire();

        Assert.Equal(1, _log.Count(WatcherRebuilt));
        Assert.Equal(1, _settled);
        Assert.Equal("font-size = 20\n", _contentAtSettle);

        // And a later save reaches the rebuilt watcher.
        _timer.WaitForBurst(() => Save("move-overwrite", "font-size = 21\n"));
        _timer.Fire();

        Assert.Equal(2, _settled);
        Assert.Equal("font-size = 21\n", _contentAtSettle);
    }

    [Fact]
    public void Rebuild_attempts_back_off_to_a_cap_and_stop_on_dispose()
    {
        var watcher = NewWatcher();

        DeleteDirectory(_dir);
        WaitUntil(() => _log.Count(WatcherError) == 1, "the watcher reported no error for its deleted directory");
        Assert.Equal(TimeSpan.FromMilliseconds(300), _timer.LastDelay);

        var delays = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            _timer.Fire();
            Assert.True(_timer.Armed, $"attempt {i + 1} scheduled no retry");
            delays.Add(_timer.LastDelay.TotalMilliseconds);
        }

        Assert.Equal(new double[] { 600, 1200, 2400, 4800, 9600, 10000, 10000 }, delays);
        Assert.Equal(ConfigFileWatcher.MaxRebuildDelay, _timer.LastDelay);
        Assert.Equal(0, _settled);

        watcher.Dispose();
        Assert.False(_timer.Armed);

        // A directory that comes back after dispose is not watched again.
        RecreateDirectory(_dir);
        _timer.Fire();
        Assert.Equal(0, _log.Count(WatcherRebuilt));
    }

    [Fact]
    public void A_second_overlapping_rebuild_attempt_does_not_leak_a_watcher()
    {
        using var watcher = NewWatcher();

        // Kill the watcher and let one attempt fail while the directory is
        // still missing. That leaves _rebuildPending true and _watcher null,
        // the exact state two overlapping timer callbacks would both see
        // right before calling TryRebuild: OnTimerFired reads
        // _rebuildPending and nulls _watcher under its own lock, separate
        // from TryRebuild's.
        DeleteDirectory(_dir);
        WaitUntil(() => _log.Count(WatcherError) == 1, "the watcher reported no error for its deleted directory");
        Assert.True(_timer.Armed);
        _timer.Fire();
        Assert.Equal(0, _log.Count(WatcherRebuilt));
        Assert.True(_timer.Armed);

        RecreateDirectory(_dir);
        File.WriteAllText(_path, "font-size = 20\n");

        // Two callbacks calling TryRebuild back to back, as if they had
        // raced into it: only the first should create a watcher.
        Assert.True(watcher.TryRebuild());
        Assert.Equal(1, _log.Count(WatcherRebuilt));

        Assert.False(watcher.TryRebuild());
        Assert.Equal(1, _log.Count(WatcherRebuilt));

        // The surviving watcher is the winner's: a real save still reaches
        // it and settles once, not twice.
        _timer.WaitForBurst(() => Save("move-overwrite", "font-size = 25\n"));
        _timer.Fire();
        Assert.Equal("font-size = 25\n", _contentAtSettle);
    }

    [Fact]
    public void A_buffer_overflow_rearms_and_warns_once_per_settle()
    {
        using var watcher = NewWatcher();

        for (var i = 0; i < 5; i++)
            watcher.HandleWatcherError(null, new InternalBufferOverflowException());

        Assert.True(_timer.Armed);
        Assert.Equal(1, _log.Count(WatcherOverflow, LogLevel.Warning));
        Assert.Equal(4, _log.Count(WatcherErrorRepeat, LogLevel.Debug));
        Assert.Equal(0, _log.Count(WatcherError));

        // The overflow may have eaten the save's own event, so the settle it
        // armed reports the file.
        _timer.Fire();
        Assert.Equal(1, _settled);

        // A new settle window warns again.
        watcher.HandleWatcherError(null, new InternalBufferOverflowException());
        Assert.Equal(2, _log.Count(WatcherOverflow, LogLevel.Warning));
    }

    [Fact]
    public void Events_raised_while_ignored_do_not_arm()
    {
        using var watcher = NewWatcher();
        _ignore = true;

        File.WriteAllText(_path, "font-size = 17\n");
        Thread.Sleep(500);

        Assert.Equal(0, _timer.ScheduleCount);
        Assert.False(_timer.Armed);
    }

    [Fact]
    public void A_synchronous_start_failure_is_warned_and_does_not_publish_a_dead_watcher()
    {
        // EnableRaisingEvents can itself raise Error synchronously, on the
        // calling thread, when the underlying watch fails right there (an
        // unsupported filesystem, or the directory vanishing between the
        // existence check and the watch being opened). TestEnable stands in
        // for that.
        using var w = new ConfigFileWatcher(
            _path,
            _timer,
            TimeSpan.FromMilliseconds(300),
            () => false,
            deliver => deliver(),
            () => Interlocked.Increment(ref _settled),
            _log);
        w.TestEnable = fsw => w.HandleWatcherError(fsw, new IOException("simulated ReadDirectoryChangesW failure"));

        Assert.False(w.Start());

        Assert.Equal(1, _log.Count(WatcherError, LogLevel.Warning));
        Assert.Equal(0, _log.Count(WatcherRebuilt));
        Assert.Equal(0, _log.Count(WatcherErrorRepeat, LogLevel.Debug));
        Assert.False(_timer.Armed);

        // Not left half-started: a real attempt right after still works.
        w.TestEnable = null;
        Assert.True(w.Start());
        _timer.WaitForBurst(() => File.WriteAllText(_path, "font-size = 24\n"));
        _timer.Fire();
        Assert.Equal(1, _settled);
    }

    [Fact]
    public void Start_refuses_a_missing_directory()
    {
        var w = new ConfigFileWatcher(
            Path.Combine(_dir, "absent", FileName),
            _timer,
            TimeSpan.FromMilliseconds(300),
            () => false,
            deliver => deliver(),
            () => { },
            NullLogger<ConfigFileWatcher>.Instance);
        using (w)
            Assert.False(w.Start());
    }

    [Fact]
    public void Dispose_stops_events_and_owns_the_timer()
    {
        var watcher = NewWatcher();
        watcher.Dispose();

        File.WriteAllText(_path, "font-size = 18\n");
        Thread.Sleep(500);
        _timer.Fire();

        Assert.Equal(0, _settled);
        Assert.True(_timer.Disposed);
    }

    [Theory]
    [MemberData(nameof(SaveShapes))]
    public void With_the_real_timer_one_save_is_one_settle(string shape)
    {
        var settled = 0;
        using var watcher = new ConfigFileWatcher(
            _path,
            new SystemSchedulerTimer(NullLogger<SystemSchedulerTimer>.Instance),
            TimeSpan.FromMilliseconds(150),
            () => false,
            deliver => deliver(),
            () => Interlocked.Increment(ref settled),
            NullLogger<ConfigFileWatcher>.Instance);
        Assert.True(watcher.Start());

        Save(shape, "font-size = 19\n");

        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref settled) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(20);
        // Well past the debounce, so a second settle from a split burst
        // would have landed by now.
        Thread.Sleep(600);

        Assert.Equal(1, Volatile.Read(ref settled));
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
    private static void DeleteDirectory(string dir) => Retry(() => Directory.Delete(dir, recursive: true));

    // A directory whose delete is still pending refuses to be created.
    private static void RecreateDirectory(string dir) => Retry(() =>
    {
        Directory.CreateDirectory(dir);
        Assert.True(Directory.Exists(dir));
    });

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

    private sealed class ListLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, int Id)> _entries = new();

        public int Count(int id) => _entries.Count(e => e.Id == id);

        public int Count(int id, LogLevel level) => _entries.Count(e => e.Id == id && e.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, eventId.Id));
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
