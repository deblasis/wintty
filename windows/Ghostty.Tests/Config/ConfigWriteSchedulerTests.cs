using System;
using System.Collections.Generic;
using Ghostty.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Config;

public class ConfigWriteSchedulerTests
{
    private sealed class FakeTimer : ISchedulerTimer
    {
        public Action? Callback { get; set; }
        public TimeSpan? LastScheduled { get; private set; }
        public int ScheduleCount { get; private set; }
        public int CancelCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void Schedule(TimeSpan delay) { LastScheduled = delay; ScheduleCount++; }
        public void Cancel() { CancelCount++; }
        public void Fire() => Callback?.Invoke();
        public void Dispose() { DisposeCount++; }
    }

    private sealed class FakeEditor : IConfigFileEditor
    {
        public string FilePath => "fake";
        public List<(string Key, string Value)> Writes { get; } = new();
        public List<string> Removes { get; } = new();
        public string ReadAll() => string.Empty;
        public void SetValue(string key, string value) => Writes.Add((key, value));
        public void RemoveValue(string key) => Removes.Add(key);
        public void WriteRaw(string content) { }
        public void SetRepeatableValues(string key, string[] values) { }
    }

    [Fact]
    public void Schedule_coalesces_same_key_and_writes_last_value()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var onFlush = 0;
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => onFlush++,
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Schedule("vertical-tabs", "false");
        scheduler.Schedule("vertical-tabs", "true");

        Assert.Empty(editor.Writes);   // not flushed yet
        Assert.Equal(3, timer.ScheduleCount); // rearmed each call

        timer.Fire();

        Assert.Single(editor.Writes);
        Assert.Equal(("vertical-tabs", "true"), editor.Writes[0]);
        Assert.Equal(1, onFlush);
    }

    [Fact]
    public void Schedule_preserves_distinct_keys()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(100), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Schedule("command-palette-background", "mica");
        scheduler.Schedule("vertical-tabs", "false");

        Assert.Equal(3, timer.ScheduleCount);

        timer.Fire();

        Assert.Equal(2, editor.Writes.Count);
        Assert.Contains(("vertical-tabs", "false"), editor.Writes);
        Assert.Contains(("command-palette-background", "mica"), editor.Writes);
    }

    [Fact]
    public void Schedule_coalesces_case_insensitively()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(100), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        // Case-insensitive: two spellings must collapse to last-wins.
        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Schedule("Vertical-Tabs", "false");
        timer.Fire();

        Assert.Single(editor.Writes);
        Assert.Equal("false", editor.Writes[0].Value);
    }

    [Fact]
    public void Dispose_flushes_writes_but_does_not_signal()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var onFlush = 0;
        var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => onFlush++,
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Dispose();

        // No flush after shutdown -- would UAF the freed ghostty app.
        Assert.Single(editor.Writes);
        Assert.Equal(0, onFlush);
    }

    [Fact]
    public void WriteBatch_continues_after_SetValue_throws()
    {
        var timer = new FakeTimer();
        var editor = new ThrowingEditor(throwOnKey: "vertical-tabs");
        var onFlush = 0;
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => onFlush++,
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("vertical-tabs", "true");    // will throw
        scheduler.Schedule("command-palette-background", "mica"); // must still land
        timer.Fire();

        Assert.Contains(("command-palette-background", "mica"), editor.Writes);
        Assert.Equal(1, onFlush);   // reload signal still fires
    }

    private sealed class ThrowingEditor : IConfigFileEditor
    {
        private readonly string _throwOnKey;
        public List<(string Key, string Value)> Writes { get; } = new();
        public string FilePath => "throwing";
        public ThrowingEditor(string throwOnKey) { _throwOnKey = throwOnKey; }
        public string ReadAll() => string.Empty;
        public void SetValue(string key, string value)
        {
            if (key == _throwOnKey) throw new InvalidOperationException("boom");
            Writes.Add((key, value));
        }
        public void RemoveValue(string key) { }
        public void WriteRaw(string content) { }
        public void SetRepeatableValues(string key, string[] values) { }
    }

    [Fact]
    public void Flush_writes_pending_synchronously()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Flush();

        Assert.Single(editor.Writes);
        Assert.Equal(1, timer.CancelCount);
    }

    [Fact]
    public void Dispose_flushes_pending_writes()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Dispose();

        Assert.Single(editor.Writes);
        Assert.Equal(1, timer.DisposeCount);
    }

    [Fact]
    public void Flush_without_pending_is_noop()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var onFlush = 0;
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => onFlush++,
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Flush();

        Assert.Empty(editor.Writes);
        Assert.Equal(0, onFlush);   // empty flush must not signal reload
        Assert.Equal(1, timer.CancelCount);
    }

    [Fact]
    public void Schedule_after_Dispose_is_noop()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Dispose();
        scheduler.Schedule("vertical-tabs", "true");
        timer.Fire();   // even if something somehow fires later

        Assert.Empty(editor.Writes);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Dispose();
        scheduler.Dispose();   // second call must not re-dispose the timer

        Assert.Equal(1, timer.DisposeCount);
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        Action noop = () => { };
        var logger = NullLogger<ConfigWriteScheduler>.Instance;

        Assert.Throws<ArgumentNullException>(() => new ConfigWriteScheduler(
            null!, timer, TimeSpan.FromMilliseconds(1), noop, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigWriteScheduler(
            editor, null!, TimeSpan.FromMilliseconds(1), noop, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(1), null!, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(1), noop, null!));
    }
}
