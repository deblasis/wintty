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
        public string[] GetRepeatableValues(string key) => System.Array.Empty<string>();
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
        public string[] GetRepeatableValues(string key) => System.Array.Empty<string>();
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

    // --- Per-key outcomes -------------------------------------------------
    //
    // A page that suppresses a repeat write of the value it last asked for
    // cannot tell a landed write from a lost one: the scheduler swallowed
    // disk failures and only logged them, so the guard moved either way and
    // re-typing the same value read as unchanged. These pin the answer the
    // page now waits for: one outcome per Schedule call, Written only for a
    // value that is in the file.

    [Fact]
    public void A_landed_value_reports_Written()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        var outcomes = new List<ConfigWriteOutcome>();
        scheduler.Schedule("log-filter", "warn", outcomes.Add);
        Assert.Empty(outcomes);   // nothing is known before the flush

        timer.Fire();

        Assert.Equal([ConfigWriteOutcome.Written], outcomes);
    }

    [Fact]
    public void A_failed_write_reports_Failed_and_the_batch_still_lands()
    {
        var timer = new FakeTimer();
        var editor = new ThrowingEditor(throwOnKey: "log-filter");
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        var failed = new List<ConfigWriteOutcome>();
        var landed = new List<ConfigWriteOutcome>();
        scheduler.Schedule("log-filter", "warn", failed.Add);
        scheduler.Schedule("log-level", "debug", landed.Add);
        timer.Fire();

        Assert.Equal([ConfigWriteOutcome.Failed], failed);
        Assert.Equal([ConfigWriteOutcome.Written], landed);
        Assert.Contains(("log-level", "debug"), editor.Writes);
    }

    [Fact]
    public void The_value_a_later_edit_replaced_reports_Superseded()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        var replaced = new List<ConfigWriteOutcome>();
        var final = new List<ConfigWriteOutcome>();
        scheduler.Schedule("quick-terminal-key", "ctrl+grave", replaced.Add);
        scheduler.Schedule("quick-terminal-key", "ctrl+shift+grave", final.Add);

        // Answered at replace time, not at the flush: the value is already
        // unreachable, and a caller waiting on its outcome would otherwise
        // wait for a flush that will not mention it.
        Assert.Equal([ConfigWriteOutcome.Superseded], replaced);

        timer.Fire();
        Assert.Equal([ConfigWriteOutcome.Written], final);
        Assert.Equal([("quick-terminal-key", "ctrl+shift+grave")], editor.Writes);
    }

    [Fact]
    public void Coalescing_is_case_insensitive_so_one_call_is_answered_Superseded()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        var first = new List<ConfigWriteOutcome>();
        var second = new List<ConfigWriteOutcome>();
        scheduler.Schedule("quick-terminal-key", "ctrl+grave", first.Add);
        scheduler.Schedule("Quick-Terminal-Key", "ctrl+alt+grave", second.Add);

        Assert.Equal([ConfigWriteOutcome.Superseded], first);

        timer.Fire();
        Assert.Equal([ConfigWriteOutcome.Written], second);
        Assert.Single(editor.Writes);
    }

    [Fact]
    public void Dispose_reports_the_drained_value_as_Written()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        var outcomes = new List<ConfigWriteOutcome>();
        scheduler.Schedule("log-filter", "warn", outcomes.Add);
        scheduler.Dispose();

        Assert.Equal([ConfigWriteOutcome.Written], outcomes);
    }

    [Fact]
    public void Scheduling_after_Dispose_reports_Superseded_rather_than_staying_silent()
    {
        // The shutdown drain runs before this, so the value is never written.
        // A callback that never fires would leave a page's guard describing a
        // write that is never going to happen.
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(200), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Dispose();

        var outcomes = new List<ConfigWriteOutcome>();
        scheduler.Schedule("log-filter", "warn", outcomes.Add);

        Assert.Equal([ConfigWriteOutcome.Superseded], outcomes);
        Assert.Empty(editor.Writes);
    }

    [Fact]
    public void A_throwing_outcome_callback_does_not_stop_the_rest_of_the_batch()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        var reported = new List<ConfigWriteOutcome>();
        scheduler.Schedule("log-filter", "warn", _ => throw new InvalidOperationException("boom"));
        scheduler.Schedule("log-level", "debug", reported.Add);
        timer.Fire();

        Assert.Equal([ConfigWriteOutcome.Written], reported);
        Assert.Equal(2, editor.Writes.Count);
    }

    [Fact]
    public void A_key_scheduled_without_a_callback_is_never_reported()
    {
        var timer = new FakeTimer();
        var editor = new FakeEditor();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(50), () => { },
            NullLogger<ConfigWriteScheduler>.Instance);

        scheduler.Schedule("log-level", "debug");
        scheduler.Schedule("log-level", "trace");
        timer.Fire();

        Assert.Single(editor.Writes);
        Assert.Equal(("log-level", "trace"), editor.Writes[0]);
    }
}
