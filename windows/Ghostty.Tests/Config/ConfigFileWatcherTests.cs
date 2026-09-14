using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Ghostty.Core.Config;
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

    private readonly string _dir;
    private readonly string _path;
    private readonly FakeTimer _timer = new();
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

    private ConfigFileWatcher NewWatcher()
    {
        var w = new ConfigFileWatcher(
            _path,
            _timer,
            TimeSpan.FromMilliseconds(300),
            ignoreEvents: () => _ignore,
            onSettled: () =>
            {
                Interlocked.Increment(ref _settled);
                _contentAtSettle = File.ReadAllText(_path);
            },
            NullLogger<ConfigFileWatcher>.Instance);
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
    public void Start_refuses_a_missing_directory()
    {
        var w = new ConfigFileWatcher(
            Path.Combine(_dir, "absent", FileName),
            _timer,
            TimeSpan.FromMilliseconds(300),
            () => false,
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

    /// <summary>
    /// Fires only when armed, as the real timer does, so a test cannot
    /// settle an edit the watcher never scheduled.
    /// </summary>
    private sealed class FakeTimer : ISchedulerTimer
    {
        private int _scheduleCount;
        private volatile bool _armed;

        public Action? Callback { get; set; }
        public bool Armed => _armed;
        public bool Disposed { get; private set; }
        public int ScheduleCount => Volatile.Read(ref _scheduleCount);

        public void Schedule(TimeSpan delay)
        {
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
